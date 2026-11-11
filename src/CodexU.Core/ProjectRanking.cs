namespace CodexU.Core;

// Indexes refer to this snapshot's Projects, so switching sorts reuses its project metadata.
public sealed record ProjectRankEntry(int ProjectIndex, double? Amount);

public sealed record ProjectRanking(
    IReadOnlyList<ProjectRankEntry> ByCost,
    IReadOnlyList<ProjectRankEntry> ByTokens,
    bool HasCost,
    bool HasEstimatedCost)
{
    public static ProjectRanking Calculate(DashboardSnapshot snapshot, AppSettings settings)
    {
        const int visibleRows = 8;
        var projects = snapshot.Projects;
        var entries = projects.Select((project, index) => new ProjectRankEntry(index,
            project.CreditsUsed is { } credits ? UsageCredits.ToAmount(credits, settings.AmountPerThousandCredits) : null))
            .ToArray();

        // OrderByDescending keeps an unknown cost below a confirmed zero and preserves source order on ties.
        return new(entries.OrderByDescending(row => row.Amount).Take(visibleRows).ToArray(),
            entries.OrderByDescending(row => projects[row.ProjectIndex].Tokens).Take(visibleRows).ToArray(),
            entries.Any(row => row.Amount.HasValue), projects.Any(p => p.CreditsUsed.HasValue && p.CostIsEstimated));
    }
}
