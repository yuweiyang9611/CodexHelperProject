using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task LoadAsync_PreservesLocalSnapshotWhenAppServerFails()
    {
        var period = new TokenPeriod(
            123,
            new TokenBreakdown(100, 20, 23, 0, 123),
            1.5,
            0,
            [],
            DataQuality.Detailed);
        var local = CreateLocalSnapshot(period);
        var service = new DashboardService(new ThrowingAppServerClient(), new StaticLocalReader(local));

        var result = await service.LoadAsync();

        Assert.Equal(123, result.Tokens.Lifetime.Tokens);
        Assert.Contains(result.Diagnostics, item => item.Contains("app-server 读取失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_MergesLocalPlanIntoPartialAppServerAccount()
    {
        var period = new TokenPeriod(
            123,
            new TokenBreakdown(100, 20, 23, 0, 123),
            1.5,
            0,
            [],
            DataQuality.Detailed);
        var localAccount = new AccountSnapshot(null, "plus", "local@example.test", false);
        var appServerAccount = new AccountSnapshot("chatgpt", null, "app@example.test", true);
        var appServer = new AppServerSnapshot(appServerAccount, null, null, ["app-ok"]);
        var service = new DashboardService(
            new StaticAppServerClient(appServer),
            new StaticLocalReader(CreateLocalSnapshot(period, localAccount)));

        var result = await service.LoadAsync();

        Assert.NotNull(result.Account);
        Assert.Equal("chatgpt", result.Account.AccountType);
        Assert.Equal("plus", result.Account.PlanType);
        Assert.Equal("app@example.test", result.Account.Email);
        Assert.True(result.Account.IsAuthenticated);
        Assert.Equal(20, result.Account.SuggestedMonthlySubscriptionAmount);
    }

    [Fact]
    public async Task LoadAsync_KeepsCompleteLocalRawTokenSummary()
    {
        var period = new TokenPeriod(
            123,
            new TokenBreakdown(100, 20, 23, 0, 123),
            1.5,
            4,
            [new ModelCreditUsage(
                "gpt-5.4",
                new TokenBreakdown(100, 20, 23, 0, 123),
                1.0,
                0.2,
                0.3,
                0.1,
                [])],
            DataQuality.Detailed);
        var appServer = new AppServerSnapshot(null, null, null, ["app-ok"]);
        var service = new DashboardService(
            new StaticAppServerClient(appServer),
            new StaticLocalReader(CreateLocalSnapshot(period)));

        var result = await service.LoadAsync();

        Assert.Equal(123, result.Tokens.Today.Tokens);
        Assert.Equal(DataQuality.Detailed, result.Tokens.Today.Quality);
        Assert.Equal(123, result.Tokens.Lifetime.Tokens);
        Assert.Equal(period.Breakdown, result.Tokens.Lifetime.Breakdown);
        Assert.Equal(1.5, result.Tokens.Lifetime.CreditsUsed);
        Assert.Equal(4, result.Tokens.Lifetime.UnratedTokens);
        Assert.Equal(period.CreditsByModel, result.Tokens.Lifetime.CreditsByModel);
        Assert.Equal(DataQuality.Detailed, result.Tokens.Lifetime.Quality);
    }

    [Fact]
    public async Task LoadAsync_KeepsClaudeTokenSummaryLocal()
    {
        var period = new TokenPeriod(
            123,
            new TokenBreakdown(100, 20, 23, 0, 123),
            1.5,
            0,
            [],
            DataQuality.Detailed);
        var claude = CreateLocalSnapshot(period);
        var service = new DashboardService(
            new StaticAppServerClient(new AppServerSnapshot(null, null, null, [])),
            new StaticLocalReader(CreateLocalSnapshot(period)),
            new StaticLocalReader(claude));

        var result = await service.LoadAsync(AgentRuntime.ClaudeCode);

        Assert.Equal(123, result.Tokens.Lifetime.Tokens);
    }

    private static LocalUsageSnapshot CreateLocalSnapshot(
        TokenPeriod period,
        AccountSnapshot? account = null) => new(
        account,
        null,
        null,
        new TokenSummary(period, period, period, period),
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        TaskLifecycleStats.Empty,
        new IndexStatus(false, 0, 0, 0, 0, null),
        ["local-ok"]);

    private sealed class ThrowingAppServerClient : IAppServerClient
    {
        public Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class StaticLocalReader(LocalUsageSnapshot snapshot) : ILocalUsageReader
    {
        public Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class StaticAppServerClient(AppServerSnapshot snapshot) : IAppServerClient
    {
        public Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}
