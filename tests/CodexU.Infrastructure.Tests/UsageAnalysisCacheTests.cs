using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageAnalysisCacheTests
{
    [Fact]
    public async Task PagingAndFilteringReuseOneReadUntilRefresh()
    {
        var reader = new Reader();
        var service = new DashboardService(new Account(), reader);
        var first = await service.QueryUsageAsync(new(PageSize: 1));
        var next = await service.QueryUsageAsync(new(Page: 2, PageSize: 1));
        Assert.Equal(1, reader.Reads);
        Assert.Same(first.Days, next.Days);
        Assert.NotEqual(first.Sessions[0].Id, next.Sessions[0].Id);
        Assert.Equal(30, next.Totals.Tokens);
        var filtered = await service.QueryUsageAsync(new(Model: "a"));
        Assert.Equal(10, filtered.Totals.Tokens);
        Assert.Equal(1, reader.Reads);

        reader.Value = Snapshot(100);
        await service.LoadAsync();
        var refreshed = await service.QueryUsageAsync(new());
        Assert.Equal(300, refreshed.Totals.Tokens);
        Assert.Equal(3, reader.Reads); // Initial query, refresh, unrestricted analysis read.
    }

    [Fact]
    public async Task RuntimesNeverShareDataAndMaintenanceInvalidatesBoth()
    {
        var codex = new Reader();
        var claude = new Reader { Value = Snapshot(100) };
        var service = new DashboardService(new Account(), codex, claude);
        Assert.Equal(30, (await service.QueryUsageAsync(new())).Totals.Tokens);
        Assert.Equal(300, (await service.QueryUsageAsync(new(AgentRuntime.ClaudeCode))).Totals.Tokens);
        service.InvalidateUsageAnalysis();
        await service.QueryUsageAsync(new());
        await service.QueryUsageAsync(new(AgentRuntime.ClaudeCode));
        Assert.Equal(2, codex.Reads);
        Assert.Equal(2, claude.Reads);
    }

    [Fact]
    public async Task FailedReadIsRetriedAndCannotServeAnEarlierRevision()
    {
        var reader = new Reader();
        var service = new DashboardService(new Account(), reader);
        await service.QueryUsageAsync(new());
        service.InvalidateUsageAnalysis();
        reader.Value = reader.Value with { AnalysisData = null };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueryUsageAsync(new()));
        reader.Value = Snapshot(100);
        Assert.Equal(300, (await service.QueryUsageAsync(new())).Totals.Tokens);
        Assert.Equal(3, reader.Reads);
    }

    [Fact]
    public async Task ConcurrentQueriesShareReadAndDiscardInvalidatedInFlightData()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { BeforeRead = async () => { started.TrySetResult(); await release.Task; } };
        var service = new DashboardService(new Account(), reader);
        var first = service.QueryUsageAsync(new());
        await started.Task;
        var second = service.QueryUsageAsync(new(Model: "b"));
        service.InvalidateUsageAnalysis();
        reader.Value = Snapshot(100);
        release.SetResult();
        Assert.Equal(300, (await first).Totals.Tokens);
        Assert.Equal(200, (await second).Totals.Tokens);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotInvalidateAnotherQuery()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new Reader { BeforeRead = () => release.Task };
        var service = new DashboardService(new Account(), reader);
        var first = service.QueryUsageAsync(new());
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.QueryUsageAsync(new(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        await first;
        await service.QueryUsageAsync(new());
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void PagingMatchesUncachedQueryAndFilterChangeReplacesSelection()
    {
        var data = Snapshot().AnalysisData;
        var query = new UsageAnalysisQuery(data);
        var first = query.Execute(new(PageSize: 1));
        var last = query.Execute(new(Page: int.MaxValue, PageSize: 1));
        Assert.Equal(2, last.Page);
        Assert.Same(first.Models, last.Models);
        var expected = UsageAnalysisQuery.Execute(data, new(Page: 2, PageSize: 1));
        Assert.Equal(expected.Sessions[0].Id, last.Sessions[0].Id);
        Assert.Equal(expected.Totals.Tokens, last.Totals.Tokens);
        var filtered = query.Execute(new(Model: "a"));
        Assert.Equal(10, filtered.Totals.Tokens);
        var restored = query.Execute(new());
        Assert.Equal(30, restored.Totals.Tokens);
        Assert.NotSame(first.Days, restored.Days);
    }

    [Fact]
    public void LargeHistoryKeepsOnlyCommonAvailableFieldsWithoutDeepIteratorChains()
    {
        var row = Snapshot().AnalysisData!.Entries[0];
        var rows = Enumerable.Range(0, 100_000).Select(i => row with
        {
            AvailableBreakdownFields = i == 50_000 ? ["input"] : ["input", "output"]
        }).ToArray();
        var result = UsageAnalysisQuery.Execute(new(rows, []), new());
        Assert.Equal(1_000_000, result.Totals.Tokens);
        Assert.Equal(["input"], result.Totals.AvailableBreakdownFields);
    }

    private static LocalUsageSnapshot Snapshot(long tokens = 10)
    {
        var empty = DashboardSnapshot.Empty(AgentRuntime.Codex);
        UsageAnalysisEntry Row(string id, string model, long count) => new(new(2026, 9, 1), id, null,
            null, null, model, "tasks", count, new(count, 0, 0, 0, count), null, null, "retained",
            AvailableBreakdownFields: ["input", "output"]);
        return new(null, null, null, empty.Tokens, [], [], [], [], [], [], [], [], empty.TaskLifecycle,
            empty.IndexStatus, [], AnalysisData: new([Row("one", "a", tokens), Row("two", "b", tokens * 2)], []));
    }

    private sealed class Reader : ILocalUsageReader
    {
        public LocalUsageSnapshot Value = Snapshot();
        public int Reads;
        public Func<Task>? BeforeRead;
        public async Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            var captured = Value;
            if (BeforeRead is not null) await BeforeRead().WaitAsync(cancellationToken);
            return captured;
        }
    }

    private sealed class Account : IAppServerClient
    {
        public Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppServerSnapshot(null, null, null, []));
    }
}
