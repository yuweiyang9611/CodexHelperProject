using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageAnalysisQueryTests
{
    private static readonly DateOnly Day = new(2025, 1, 10);
    private static readonly ModelCreditRate Rate = new("model-a", 100, 10, 200, new(2020, 1, 1), "test", "v1");

    [Fact]
    public void AllHistory_HasNoSnapshotHorizon_AndEveryViewUsesTheSameContributions()
    {
        var data = UsageHistoryProjection.BuildAnalysis([
            Row("old", Day.AddDays(-500), 40), Row("parent", Day, 100),
            Row("child", Day, 50) with { ParentSessionId = "parent", Feature = "subagents" },
            Row("other", Day.AddDays(1), 30, "model-b", "D:/Other")],
            [new(Day, new(200, 0, 0, 0, 200), 900, 0, DataQuality.Approximate)], [Rate], true);
        var result = UsageAnalysisQuery.Execute(data, new());
        Assert.Equal(Day.AddDays(-500), result.AvailableFrom);
        Assert.Equal(270, result.Totals.Tokens);
        Assert.Equal(50, result.Totals.UnattributedTokens);
        Assert.Equal(220, result.Totals.BreakdownTokens);
        Assert.Equal(result.Totals.Tokens, result.Days.Sum(e => e.Totals.Tokens));
        Assert.Equal(result.Totals.Tokens, result.Models.Sum(e => e.Totals.Tokens));
        Assert.Equal(result.Totals.Tokens, result.Features.Sum(e => e.Totals.Tokens));
        Assert.Equal(result.Totals.Tokens, result.Projects.Sum(e => e.Totals.Tokens));
        Assert.Equal(result.Totals.Tokens, result.Sessions.Sum(e => e.Totals.Tokens) + result.Unattributed.Sum(e => e.Tokens));
        Assert.Equal(900, Assert.Single(result.LegacyEstimates).CreditsUsed);
        Assert.Equal(0.019, result.Totals.CreditsUsed!.Value, 10);
    }

    [Fact]
    public void CombinedFilters_DoNotChangeChoices_OrLoseAFilteredParentsIdentity()
    {
        var data = UsageHistoryProjection.BuildAnalysis([
            Row("root", Day.AddDays(-1), 100, "model-b"),
            Row("child", Day, 20) with { ParentSessionId = "root", Feature = "subagents" },
            Row("adjacent", Day, 60, "model-a", "D:/Project2"),
            Row("other-model", Day, 50, "model-b"), Row("later", Day.AddDays(1), 70)], [], [Rate], true);
        var result = UsageAnalysisQuery.Execute(data, new(From: Day, To: Day, Model: "MODEL-A", Project: "d:/project"));
        Assert.Equal(20, result.Totals.Tokens);
        Assert.Equal(new[] { "model-a", "model-b" }, result.AvailableModels);
        Assert.Equal(2, result.AvailableProjects.Count);
        var group = Assert.Single(result.Sessions);
        Assert.Equal("root", group.Id);
        Assert.Equal("child", Assert.Single(group.Members).Id);
        Assert.Equal("root", group.Members[0].ParentSessionId);
        Assert.Equal(Day, group.From);
        Assert.Empty(result.Unattributed);
    }

    [Fact]
    public void Pagination_AppliesToGroups_WithoutSplittingChildrenOrChangingDenominators()
    {
        var data = UsageHistoryProjection.BuildAnalysis([
            Row("a", Day, 100), Row("b", Day, 70), Row("c", Day, 10),
            Row("child", Day, 5) with { ParentSessionId = "a" },
            Row("grandchild", Day, 3) with { ParentSessionId = "child" }], [], [Rate], true);
        var first = UsageAnalysisQuery.Execute(data, new(PageSize: 1));
        var second = UsageAnalysisQuery.Execute(data, new(Page: 2, PageSize: 1));
        Assert.Equal(188, first.Totals.Tokens);
        Assert.Equal(first.Totals.Tokens, second.Totals.Tokens);
        Assert.Equal(first.Totals.CreditsUsed, second.Totals.CreditsUsed);
        Assert.Equal(first.Totals.Breakdown, second.Totals.Breakdown);
        Assert.Equal(first.Totals.AvailableBreakdownFields, second.Totals.AvailableBreakdownFields);
        Assert.Equal(3, first.SessionCount);
        Assert.Equal(3, Assert.Single(first.Sessions).Members.Count);
        Assert.Equal(108, first.Sessions[0].Totals.Tokens);
        Assert.Equal("b", Assert.Single(second.Sessions).Id);
        Assert.Equal(3, UsageAnalysisQuery.Execute(data, new(Page: int.MaxValue, PageSize: 1)).Page);
    }

    [Theory]
    [InlineData("D:/Project/")]
    [InlineData("D:/Project/child/..")]
    public void ProjectFilter_MatchesTheReadersNormalizedPath(string requestedProject)
    {
        var storedProject = WorkspaceScope.Normalize("D:/Project");
        var data = UsageHistoryProjection.BuildAnalysis([
            Row("parent", Day, 100, project: storedProject),
            Row("child", Day, 20, project: storedProject) with { ParentSessionId = "parent" },
            Row("adjacent", Day, 60, project: storedProject + "2")], [], [Rate], true);
        var result = UsageAnalysisQuery.Execute(data, new(Model: "model-a", Project: requestedProject));
        Assert.Equal(120, result.Totals.Tokens);
        Assert.Equal(2, Assert.Single(result.Sessions).Members.Count);
    }

    [Fact]
    public void ProjectFilter_ExpandsWindowsShortNamesLikeTheReader()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Existing system alias needs no fixture directory or privileged writes.
        var aliasDirectory = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "PROGRA~1");
        if (!Directory.Exists(aliasDirectory)) return;
        var requestedProject = Path.Combine(aliasDirectory, "codexu-query-fixture");
        var storedProject = WorkspaceScope.Normalize(requestedProject);
        var data = UsageHistoryProjection.BuildAnalysis([Row("parent", Day, 120, project: storedProject)], [], [Rate], true);
        Assert.Equal(120, UsageAnalysisQuery.Execute(data, new(Model: "model-a", Project: requestedProject)).Totals.Tokens);
    }

    [Fact]
    public void ProjectFilter_InvalidPathCannotBecomeAnUnfilteredQuery()
    {
        var data = UsageHistoryProjection.BuildAnalysis([Row("parent", Day, 120)], [], [Rate], true);
        Assert.Throws<ArgumentException>(() => UsageAnalysisQuery.Execute(data, new(Project: "invalid\0path")));
    }

    [Fact]
    public void LegacyRemainder_IsNotAFakeSession_AndIsNotAllocatedToAProjectOrModel()
    {
        var data = UsageHistoryProjection.BuildAnalysis([Row("known", Day, 40)],
            [new(Day, new(100, 0, 0, 0, 100), 25, 0, DataQuality.Detailed)], [Rate], true);
        var all = UsageAnalysisQuery.Execute(data, new());
        var residual = Assert.Single(all.Unattributed);
        Assert.Null(residual.SessionId);
        Assert.Null(residual.Project);
        Assert.Null(residual.Breakdown);
        Assert.Null(residual.CreditsUsed);
        Assert.NotNull(residual.Explanation);
        Assert.Single(all.Sessions);
        var project = UsageAnalysisQuery.Execute(data, new(Project: "D:/Project"));
        var model = UsageAnalysisQuery.Execute(data, new(Model: "model-a"));
        var unknown = UsageAnalysisQuery.Execute(data, new(Project: UsageAnalysisQuery.UnknownProject));
        Assert.Equal(40, project.Totals.Tokens);
        Assert.Equal(40, model.Totals.Tokens);
        Assert.Equal(60, unknown.Totals.Tokens);
        Assert.Null(unknown.Totals.CreditsUsed);
        Assert.Empty(project.LegacyEstimates);
        Assert.Empty(model.LegacyEstimates);
    }

    [Fact]
    public void Repricing_UsesApplicableDate_KeepsLegacyEstimateSeparate_AndDistinguishesFreeFromMissing()
    {
        var old = Row("old", Day.AddDays(-1), 10);
        var current = Row("current", Day, 20);
        var free = Row("free", Day, 30, "free");
        var rates = new[] { Rate with { EffectiveFrom = Day }, new ModelCreditRate("free", 0, 0, 0, Day) };
        var data = UsageHistoryProjection.BuildAnalysis([old, current, free],
            [new(Day, new(100, 0, 0, 0, 100), 900, 0, DataQuality.Approximate)], rates, true);
        var result = UsageAnalysisQuery.Execute(data, new());
        Assert.Equal(50, result.Totals.RatedTokens);
        Assert.Equal(60, result.Totals.UnratedTokens);
        Assert.Equal(0.002, result.Totals.CreditsUsed!.Value, 10);
        Assert.Null(UsageAnalysisQuery.Execute(data, new(To: Day.AddDays(-1))).Totals.CreditsUsed);
        Assert.Equal(0, UsageAnalysisQuery.Execute(data, new(Model: "free")).Totals.CreditsUsed);
        Assert.Equal(Day, data.Entries.Single(e => e.SessionId == "current").Rate!.EffectiveFrom);
        var repriced = UsageHistoryProjection.BuildAnalysis([old, current, free],
            [new(Day, new(100, 0, 0, 0, 100), 900, 0, DataQuality.Approximate)],
            [rates[0] with { InputCreditsPerMillion = 200 }, rates[1]], true);
        Assert.Equal(result.Totals.Tokens, UsageAnalysisQuery.Execute(repriced, new()).Totals.Tokens);
        Assert.Equal(0.004, UsageAnalysisQuery.Execute(repriced, new()).Totals.CreditsUsed!.Value, 10);
        Assert.Equal(900, Assert.Single(repriced.LegacyEstimates).CreditsUsed);
    }

    [Fact]
    public void UnverifiedMissingOrCyclicParents_RemainSeparateWithoutLosingTokens()
    {
        var data = UsageHistoryProjection.BuildAnalysis([
            Row("a", Day, 10) with { ParentSessionId = "b" },
            Row("b", Day, 20) with { ParentSessionId = "a" },
            Row("missing-parent", Day, 30) with { ParentSessionId = "missing" }], [], [Rate], true);
        var result = UsageAnalysisQuery.Execute(data, new());
        Assert.Equal(3, result.Sessions.Count);
        Assert.Equal(60, result.Sessions.Sum(e => e.Totals.Tokens));
    }

    [Fact]
    public void EmptyOrInvalidQueries_AreExplicit()
    {
        var result = UsageAnalysisQuery.Execute(null, new());
        Assert.Empty(result.Sessions);
        Assert.Null(result.Totals.CreditsUsed);
        Assert.Null(result.AvailableFrom);
        Assert.Throws<ArgumentException>(() => UsageAnalysisQuery.Execute(null, new(From: Day.AddDays(1), To: Day)));
        Assert.Throws<ArgumentException>(() => UsageAnalysisQuery.Execute(null, new(Page: 0)));
        Assert.Throws<ArgumentException>(() => UsageAnalysisQuery.Execute(null, new(PageSize: 101)));
        Assert.Throws<ArgumentException>(() => UsageAnalysisQuery.Execute(null, new(Runtime: (AgentRuntime)99)));
    }

    [Fact]
    public void AppliedRate_UsesRawModelBeforeAliasNormalization()
    {
        var rates = new[] { new ModelCreditRate("gpt-5.2-codex", 8, 0.8, 80, MatchMode: "prefix") };
        var row = Row("alias", new(2026, 9, 20), 100, "gpt-5.2-codex-latest");
        var expected = UsageCredits.Calculate([new DatedModelTokenUsage(row.Date, row.Model, row.Tokens)], rates, true);
        var data = UsageHistoryProjection.BuildAnalysis([row], [], rates, true);
        Assert.Equal("gpt-5.2", Assert.Single(data.Entries).Model);
        Assert.Equal("gpt-5.2-codex", data.Entries[0].Rate!.Model);
        Assert.Equal(expected.CreditsUsed, data.Entries[0].CreditsUsed);
    }

    private static AttributedUsage Row(string source, DateOnly date, long tokens, string model = "model-a", string? project = "D:/Project") =>
        new(source, project, date, model, new(tokens, 0, 0, 0, tokens), 1, Feature: "tasks",
            AvailableFields: UsageBreakdownFields.Input | UsageBreakdownFields.Output | UsageBreakdownFields.CachedInput);
}
