using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class ProjectRankingTests
{
    [Fact]
    public void RanksTopEightByKnownAmountOrTokensAndKeepsZeroAboveUnknown()
    {
        var projects = Enumerable.Range(0, 10)
            .Select(i => new ProjectUsage(i.ToString(), "project", null, i + 1, 1, null, null,
                i switch { 0 => 0, 1 => 50, _ => null }, DataQuality.Detailed)).ToArray();
        projects[1] = projects[1] with { UnratedTokens = 1 };
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.ClaudeCode) with { Projects = projects };

        var ranking = ProjectRanking.Calculate(snapshot, new AppSettings(AmountPerThousandCredits: 20));
        Assert.True(ranking.HasCost);
        Assert.Equal(new[] { 1, 0, 2, 3, 4, 5, 6, 7 }, ranking.ByCost.Select(r => r.ProjectIndex));
        Assert.Equal(1d, ranking.ByCost[0].Amount);
        Assert.Equal(0d, ranking.ByCost[1].Amount);
        Assert.Null(ranking.ByCost[2].Amount);
        Assert.Equal(new[] { 9, 8, 7, 6, 5, 4, 3, 2 }, ranking.ByTokens.Select(r => r.ProjectIndex));

        Assert.True(ProjectRanking.Calculate(snapshot with { Projects = [projects[0]] }, new AppSettings()).HasCost);
        Assert.False(ProjectRanking.Calculate(snapshot with { Projects = [projects[2]] }, new AppSettings()).HasCost);
    }
}
