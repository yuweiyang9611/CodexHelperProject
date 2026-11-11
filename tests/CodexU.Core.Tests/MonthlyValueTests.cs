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
