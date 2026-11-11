using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class CombinedRuntimeSummaryTests
{
    private static readonly AppSettings Settings = new(CodexAutoDetectSubscriptionAmount: false,
        CodexMonthlySubscriptionAmount: 50, ClaudeAutoDetectSubscriptionAmount: false, ClaudeMonthlySubscriptionAmount: 50);

    [Fact]
    public void DistinguishesMissingRuntimeFromFailedReadAndKeepsPeriodsIndependent()
    {
        var codex = Read(AgentRuntime.Codex);
        var absent = new RuntimeReadResult(DashboardSnapshot.Empty(AgentRuntime.ClaudeCode), false);
        var result = CombinedRuntimeSummary.Project(codex, absent, Settings);
        Assert.Equal(RuntimeContribution.Absent, result.ClaudeCode.Contribution);
        Assert.Equal(100, result.Summary.Periods.Month.Tokens);
        Assert.Equal(5, result.Summary.Periods.Today.Tokens);
        Assert.Equal(50d, result.Summary.Cost.SubscriptionAmount);
        Assert.Equal(40d, result.Summary.Cost.KnownAmount);
        Assert.Equal(0.8d, result.Summary.Cost.PaybackMultiple);
        Assert.Equal(DataQuality.Detailed, result.Summary.Periods.Month.Quality);
        Assert.False(result.Summary.Cost.IsLowerBound);

        result = CombinedRuntimeSummary.Project(codex, absent with { ReadFailed = true }, Settings);
        Assert.Equal(RuntimeContribution.Failed, result.ClaudeCode.Contribution);
        Assert.Equal([AgentRuntime.ClaudeCode], result.Summary.FailedRuntimes);
        Assert.Equal(100, result.Summary.Periods.Month.Tokens);
        Assert.Equal(DataQuality.Partial, result.Summary.Periods.Month.Quality);
        Assert.True(result.Summary.Cost.IsLowerBound);
        Assert.Null(result.Summary.Cost.SubscriptionAmount);
        Assert.Null(result.Summary.Cost.PaybackMultiple);
    }

    [Theory]
    [InlineData(1000, 80, null)]
    [InlineData(2000, 120, 1.2)]
    public void PartlyPricedUsageOnlyProvesPaybackWhenTheKnownAmountCoversBothFees(double claudeCredits,
        double known, double? multiple)
    {
        var result = CombinedRuntimeSummary.Project(Read(AgentRuntime.Codex),
            Read(AgentRuntime.ClaudeCode, claudeCredits, 20), Settings).Summary;
        Assert.Equal(known, result.Cost.KnownAmount);
        Assert.Equal(100d, result.Cost.SubscriptionAmount);
        Assert.True(result.Cost.IsLowerBound);
        Assert.Equal(multiple, result.Cost.PaybackMultiple);
        Assert.Equal(multiple is null ? CombinedPaybackReason.IncompleteUsage : CombinedPaybackReason.None, result.Cost.PaybackReason);
    }

    [Fact]
    public void UnknownPriceOrZeroSubscriptionNeverCreatesARatio()
    {
        var result = CombinedRuntimeSummary.Project(Read(AgentRuntime.Codex), Read(AgentRuntime.ClaudeCode),
            Settings with { ClaudeAutoDetectSubscriptionAmount = true }).Summary;
        Assert.Equal([AgentRuntime.ClaudeCode], result.Cost.UnknownSubscriptions);
        Assert.Null(result.Cost.NetAmount);
        Assert.Equal(CombinedPaybackReason.UnknownSubscription, result.Cost.PaybackReason);
        result = CombinedRuntimeSummary.Project(Read(AgentRuntime.Codex), Read(AgentRuntime.ClaudeCode),
            Settings with { ClaudeMonthlySubscriptionAmount = 0, CodexMonthlySubscriptionAmount = 0 }).Summary;
        Assert.Equal(0d, result.Cost.SubscriptionAmount);
        Assert.Null(result.Cost.PaybackMultiple);
        Assert.Equal(CombinedPaybackReason.ZeroSubscription, result.Cost.PaybackReason);
    }

    [Fact]
    public void MergesVerifiedPathsPreservesUnknownCostsAndRanksByTokens()
    {
        var epoch = DateTimeOffset.UnixEpoch;
        ProjectUsage Row(string id, string path, int tokens, double? credits) =>
            new(id, id, path, tokens, 1, epoch, "old", credits, DataQuality.Detailed);
        var shared = Row("shared", @"\\Server\Share\App\", 10, 10);
        const string store = @"D:\.claude\projects\session";
        var codex = Read(AgentRuntime.Codex);
        codex = codex with { Snapshot = codex.Snapshot with { Projects = [shared, Row("store1", store, 8, 10), Row("bare1", "D:", 1, 5)] } };
        var claude = Read(AgentRuntime.ClaudeCode);
        claude = claude with
        {
            Snapshot = claude.Snapshot with
            {
                Projects = [Row("other", "//server/share/app", 13, null) with { LastActiveAt = epoch.AddHours(2), Branch = "new", CostIsEstimated = true },
                    Row("store2", store, 8, 10), Row("bare2", "D:", 2, 10)]
            }
        };
        var projects = CombinedRuntimeSummary.Project(codex, claude, Settings).Summary.Projects;
        Assert.Equal(5, projects.Count);
        Assert.Equal("shared", projects[0].Id);
        Assert.Equal(23, projects[0].Tokens);
        Assert.Equal("new", projects[0].Branch);
        Assert.Equal(0.4d, projects[0].Amount);
        Assert.Equal(ProjectCostStatus.Partial, projects[0].CostStatus);
        Assert.False(projects[0].CostIsEstimated);
        Assert.Equal([AgentRuntime.Codex, AgentRuntime.ClaudeCode], projects[0].Runtimes);
        Assert.Equal(1, projects[0].ThreadCounts[AgentRuntime.Codex]);
        Assert.Equal(1, projects[0].ThreadCounts[AgentRuntime.ClaudeCode]);
    }

    [Fact]
    public void MergedProjectsSeparateFreePartialAndUnknownAmounts()
    {
        ProjectUsage Row(string name, double? credits, long unpriced = 0) =>
            new(name, name, @"D:\Repo\" + name, 10, 1, null, null, credits,
                DataQuality.Detailed, UnratedTokens: unpriced);
        var left = Read(AgentRuntime.Codex);
        left = left with { Snapshot = left.Snapshot with { Projects = [Row("free", 0), Row("mixed", 0), Row("unknown", null)] } };
        var right = Read(AgentRuntime.ClaudeCode);
        right = right with { Snapshot = right.Snapshot with { Projects = [Row("free", 0), Row("mixed", 100, 5), Row("unknown", null)] } };

        var result = CombinedRuntimeSummary.Project(left, right, Settings).Summary.Projects.ToDictionary(p => p.Id);
        Assert.Equal((0d, ProjectCostStatus.Known), (result["free"].Amount!.Value, result["free"].CostStatus));
        Assert.Equal((4d, ProjectCostStatus.Partial), (result["mixed"].Amount!.Value, result["mixed"].CostStatus));
        Assert.Null(result["unknown"].Amount);
        Assert.Equal(ProjectCostStatus.Unknown, result["unknown"].CostStatus);

        var failed = CombinedRuntimeSummary.Project(left, right with { ReadFailed = true }, Settings).Summary.Projects;
        Assert.Equal(ProjectCostStatus.Partial, failed.Single(p => p.Id == "free").CostStatus);
    }

    [Fact]
    public void OrderedForecastPhasesExcludeDesktopSourcesAndChangeAtResetWithoutAnotherRead()
    {
        var epoch = DateTimeOffset.UnixEpoch;
        var codex = Read(AgentRuntime.Codex);
        QuotaForecast Forecast(int hours) => new(1, TimeSpan.FromHours(hours), epoch.AddHours(hours), true, TimeSpan.FromHours(1));
        var five = new RateLimitWindow(50, 300, epoch.AddHours(2));
        codex = codex with
        {
            Snapshot = codex.Snapshot with
            {
                PrimaryQuota = five,
                PrimaryForecast = Forecast(1),
                SecondaryQuota = five with { ResetsAt = epoch.AddDays(1) },
                SecondaryForecast = Forecast(3),
                Diagnostics = ["first"],
                RefreshedAt = epoch.AddHours(1)
            }
        };
        var claude = Read(AgentRuntime.ClaudeCode);
        claude = claude with
        {
            Snapshot = claude.Snapshot with
            {
                PrimaryQuota = five with { Source = "claude-desktop-get-usage" },
                PrimaryForecast = Forecast(0),
                Diagnostics = ["first"],
                RefreshedAt = epoch
            }
        };
        var summary = CombinedRuntimeSummary.Project(codex, claude, Settings).Summary;
        Assert.Equal(["Codex：first", "Claude Code：first"], summary.Diagnostics);
        Assert.Equal(epoch, summary.RefreshedAt);
        Assert.Collection(summary.Forecasts,
            first =>
            {
                Assert.Equal(2, first.Total);
                Assert.Equal(2, first.Predictable);
                Assert.False(first.Weekly);
                Assert.Equal(epoch.AddHours(2), first.ValidUntil);
            },
            second =>
            {
                Assert.Equal(1, second.Predictable);
                Assert.True(second.Weekly);
                Assert.Equal(epoch.AddDays(1), second.ValidUntil);
            });
    }

    private static RuntimeReadResult Read(AgentRuntime runtime, double credits = 1000, long unrated = 0)
    {
        var snapshot = DashboardSnapshot.Empty(runtime);
        var month = snapshot.Tokens.Month with { Tokens = 100, CreditsUsed = credits, UnratedTokens = unrated, Quality = DataQuality.Detailed };
        return new(snapshot with { Tokens = snapshot.Tokens with { Month = month, Today = month with { Tokens = 5 } } }, false);
    }
}
