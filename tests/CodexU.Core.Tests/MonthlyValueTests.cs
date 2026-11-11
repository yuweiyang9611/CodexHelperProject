using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class MonthlyValueTests
{
    [Theory]
    [InlineData(2000, 0, PaybackStatus.Below, -20d, 0.8)]
    [InlineData(2000, 200, PaybackStatus.Unknown, null, null)]
    [InlineData(3000, 200, PaybackStatus.Reached, 20d, 1.2)]
    public void ManualSubscriptionUsesOnlyKnownCosts(
        double credits, long unpriced, PaybackStatus expected, double? net, double? multiple)
    {
        var value = MonthlyValue.Calculate(Snapshot(credits, unpriced),
            new AppSettings(ClaudeAutoDetectSubscriptionAmount: false, ClaudeMonthlySubscriptionAmount: 100));
        Assert.Equal(100d, value.SubscriptionAmount);
        Assert.Equal(SubscriptionSource.Manual, value.SubscriptionSource);
        Assert.Equal(UsageCredits.ToAmount(credits), value.KnownAmount);
        Assert.Equal(unpriced > 0, value.IsLowerBound);
        Assert.Equal(expected, value.PaybackStatus);
        Assert.Equal(net, value.NetAmount);
        Assert.Equal(multiple, value.PaybackMultiple);
        Assert.Equal(100d * (1000 - unpriced) / 1000, value.CoveragePercent);
        Assert.Equal(unpriced == 0, value.ProjectedAmount.HasValue);
    }

    [Fact]
    public void UnknownAutomaticPriceCannotReuseSavedManualAmount()
    {
        var value = MonthlyValue.Calculate(Snapshot(2000, 0), new AppSettings(ClaudeMonthlySubscriptionAmount: 100));
        Assert.Equal(80d, value.KnownAmount);
        Assert.Equal(SubscriptionSource.Unknown, value.SubscriptionSource);
        Assert.Null(value.SubscriptionAmount);
        Assert.Equal(PaybackStatus.Unknown, value.PaybackStatus);
        Assert.Null(value.PaybackMultiple);
    }

    [Fact]
    public void UsesRuntimeSpecificPriceAndSnapshotMonthForProjection()
    {
        var snapshot = Snapshot(1000, 0) with
        {
            Runtime = AgentRuntime.Codex,
            Account = new AccountSnapshot("chatgpt", "plus", null, true),
            RefreshedAt = new DateTimeOffset(new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Local))
        };
        var settings = new AppSettings(AmountPerThousandCredits: 50, ClaudeAutoDetectSubscriptionAmount: false);
        var value = MonthlyValue.Calculate(snapshot, settings);
        Assert.Equal(SubscriptionSource.Auto, value.SubscriptionSource);
        Assert.Equal(snapshot.Account.SuggestedMonthlySubscriptionAmount, value.SubscriptionAmount);
        Assert.Equal(50d, value.KnownAmount);
        Assert.Equal(100d, value.ProjectedAmount);
    }

    [Fact]
    public void FreeSubscriptionHasNoDivisorAndUnavailableUsageHasNoPayback()
    {
        var settings = new AppSettings(ClaudeAutoDetectSubscriptionAmount: false, ClaudeMonthlySubscriptionAmount: 0);
        var value = MonthlyValue.Calculate(Snapshot(100, 0), settings);
        Assert.Equal(0d, value.SubscriptionAmount);
        Assert.Equal(PaybackStatus.Reached, value.PaybackStatus);
        Assert.Null(value.PaybackMultiple);
        var missing = MonthlyValue.Calculate(DashboardSnapshot.Empty(AgentRuntime.ClaudeCode), settings);
        Assert.Equal(PaybackStatus.Unknown, missing.PaybackStatus);
        Assert.Null(missing.NetAmount);
        Assert.Null(missing.ProjectedAmount);
    }

    [Fact]
    public void DollarBreakdownReconcilesModelsAndHistoricalVersionsAtTheSavedConversion()
    {
        var recorded = new DateOnly(2026, 10, 1);
        var first = new RateCreditUsage("v1", "catalog", null, TokenBreakdown.Zero, 10, 2, 30, 5);
        var second = new RateCreditUsage("v2", "custom", recorded, TokenBreakdown.Zero, 3, 1, 4, 2, 7);
        var models = new ModelCreditUsage[]
        {
            new("first", TokenBreakdown.Zero, 13, 3, 34, 7, [first, second], 7),
            new("second", TokenBreakdown.Zero, 1, 3, 4, 6, [])
        };
        var snapshot = Snapshot(65, 200);
        snapshot = snapshot with { Tokens = snapshot.Tokens with { Month = snapshot.Tokens.Month with { CreditsByModel = models } } };
        var value = MonthlyValue.Calculate(snapshot, new AppSettings(AmountPerThousandCredits: 50));
        Assert.True(value.IsLowerBound);
        var details = Assert.IsType<MonthlyCostBreakdown>(value.Breakdown);
        Assert.Equal(1.05, details.InputAndWrites, 12);
        Assert.Equal(0.3, details.CachedInput, 12);
        Assert.Equal(1.9, details.Output, 12);
        Assert.Equal(0.65, details.CachedSavings, 12);
        Assert.Equal(value.KnownAmount!.Value, details.InputAndWrites + details.CachedInput + details.Output, 12);
        Assert.Equal(value.KnownAmount.Value, value.Models.Sum(m => m.Total), 12);
        var model = value.Models[0];
        Assert.Equal(model.Total, model.RateVersions.Sum(v => v.Total), 12);
        Assert.Equal(0.35, model.CacheWrite, 12);
        Assert.Equal(new RateVersionAmount("v2", "custom", recorded, 0.75), model.RateVersions[1]);
    }

    [Fact]
    public void EntirelyUnpricedMonthHasNoDollarAmountsButZeroRatesAreKnown()
    {
        var unpriced = MonthlyValue.Calculate(Snapshot(0, 1000), new AppSettings());
        Assert.Null(unpriced.KnownAmount);
        Assert.Null(unpriced.Breakdown);
        Assert.Equal(PaybackStatus.Unknown, unpriced.PaybackStatus);
        var free = Snapshot(0, 0);
        free = free with
        {
            Tokens = free.Tokens with
            {
                Month = free.Tokens.Month with { CreditsByModel = [new("free", TokenBreakdown.Zero, 0, 0, 0, 0, [])] }
            }
        };
        var priced = MonthlyValue.Calculate(free, new AppSettings());
        Assert.Equal(0d, priced.KnownAmount);
        Assert.Equal(new MonthlyCostBreakdown(0, 0, 0, 0), priced.Breakdown);
    }

    private static DashboardSnapshot Snapshot(double credits, long unpriced)
    {
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.ClaudeCode);
        return snapshot with
        {
            Tokens = snapshot.Tokens with
            {
                Month = snapshot.Tokens.Month with
                {
                    Tokens = 1000,
                    CreditsUsed = credits,
                    UnratedTokens = unpriced,
                    Quality = DataQuality.Detailed
                }
            }
        };
    }
}
