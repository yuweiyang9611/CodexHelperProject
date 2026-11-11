namespace CodexU.Core;

public enum RuntimeContribution { Present, Absent, Failed }

public enum CombinedPaybackReason { None, UnknownSubscription, ZeroSubscription, IncompleteUsage }

public sealed record CombinedPeriod(long Tokens, long UnratedTokens, double CreditsUsed, double? Amount,
    DataQuality Quality, bool CreditsAreLowerBound);

public sealed record CombinedPeriods(CombinedPeriod Today, CombinedPeriod SevenDays, CombinedPeriod Month);

public sealed record CombinedCost(double? KnownAmount, bool IsLowerBound, double? SubscriptionAmount,
    IReadOnlyList<AgentRuntime> UnknownSubscriptions, bool HasManual, double? PaybackMultiple,
    double? NetAmount, CombinedPaybackReason PaybackReason);

public sealed record CombinedProject(string Id, string Name, string? Branch, long Tokens, double? Amount, ProjectCostStatus CostStatus,
    bool CostIsEstimated, DateTimeOffset? LastActiveAt, IReadOnlyList<AgentRuntime> Runtimes,
    IReadOnlyDictionary<AgentRuntime, int> ThreadCounts, DataQuality Quality);

// Each card switches at ValidUntil without re-reading either source on the minute tick.
public sealed record CombinedForecast(AgentRuntime Runtime, bool Weekly, DateTimeOffset ExhaustsAt,
    DateTimeOffset? ValidUntil, int Predictable, int Total);

public sealed record CombinedRuntimeSummary(CombinedPeriods Periods, CombinedCost Cost,
    IReadOnlyList<CombinedProject> Projects, IReadOnlyList<CombinedForecast> Forecasts,
    IReadOnlyList<string> Diagnostics, DateTimeOffset? RefreshedAt, IReadOnlyList<AgentRuntime> FailedRuntimes)
{
    public static CombinedSnapshots Project(RuntimeReadResult codex, RuntimeReadResult claude, AppSettings settings)
    {
        // Settings may change between the two asynchronous reads. Use one price basis.
        RuntimeReadResult Revalue(RuntimeReadResult read) => read.ReadFailed ? read : read with
        {
            Snapshot = read.Snapshot.WithValuation(settings)
        };
        codex = Revalue(codex);
        claude = Revalue(claude);
        RuntimeReadResult[] reads = [codex, claude];
        var present = reads.Where(r => r.Contribution == RuntimeContribution.Present).Select(r => r.Snapshot).ToArray();
        var failed = reads.Where(r => r.ReadFailed).Select(r => r.Snapshot.Runtime).ToArray();
        CombinedPeriod Period(Func<TokenSummary, TokenPeriod> select)
        {
            var rows = present.Select(s => select(s.Tokens)).ToArray();
            var quality = rows.Select(p => p.Quality).DefaultIfEmpty(DataQuality.Unavailable).Max();
            if (failed.Length > 0 && quality == DataQuality.Detailed) quality = DataQuality.Partial;
            var known = rows.Where(p => p.Valuation?.KnownAmount is not null).ToArray();
            var partial = failed.Length > 0 || known.Length != rows.Length || rows.Any(p => p.UnratedTokens > 0);
            var credits = known.Sum(p => p.CreditsUsed);
            return new(rows.Sum(p => p.Tokens), rows.Sum(p => p.UnratedTokens), credits,
                known.Length == 0 ? null : UsageCredits.ToAmount(credits, settings.AmountPerThousandCredits), quality, partial);
        }
        var periods = new CombinedPeriods(Period(p => p.Today), Period(p => p.SevenDays), Period(p => p.Month));
        return new(codex, claude, new(periods, ProjectCost(reads, periods.Month), MergeProjects(present, settings, failed.Length > 0),
            ForecastsFor(present), reads.SelectMany(r => r.Snapshot.Diagnostics.Select(d =>
                (r.Snapshot.Runtime == AgentRuntime.Codex ? "Codex：" : "Claude Code：") + d)).ToArray(),
            present.Min(s => (DateTimeOffset?)s.RefreshedAt), failed));
    }

    private static CombinedCost ProjectCost(RuntimeReadResult[] reads, CombinedPeriod month)
    {
        var included = reads.Where(r => r.Contribution != RuntimeContribution.Absent).ToArray();
        var unknown = included.Where(r => r.ReadFailed || r.Snapshot.MonthlyValue?.SubscriptionAmount is null)
            .Select(r => r.Snapshot.Runtime).ToArray();
        double? subscription = included.Length > 0 && unknown.Length == 0
            ? included.Sum(r => r.Snapshot.MonthlyValue!.SubscriptionAmount!.Value) : null;
        var reason = subscription is null ? CombinedPaybackReason.UnknownSubscription
            : subscription == 0 ? CombinedPaybackReason.ZeroSubscription
            : month.Amount is null || (month.CreditsAreLowerBound && month.Amount < subscription)
                ? CombinedPaybackReason.IncompleteUsage : CombinedPaybackReason.None;
        return new(month.Amount, month.CreditsAreLowerBound, subscription, unknown,
            included.Any(r => !r.ReadFailed && r.Snapshot.MonthlyValue?.SubscriptionSource == SubscriptionSource.Manual),
            reason == CombinedPaybackReason.None ? month.Amount / subscription : null,
            reason == CombinedPaybackReason.None ? month.Amount - subscription : null, reason);
    }

    private static IReadOnlyList<CombinedProject> MergeProjects(DashboardSnapshot[] snapshots, AppSettings settings, bool missingRuntime)
    {
        var projects = snapshots.SelectMany(s => s.Projects.Select(p => (s.Runtime, Project: p))).ToArray();
        // An unidentified path stays a separate row even when another project shares its label.
        return projects.Select((row, index) => (row, key: ProjectKey(row.Project.FullPath) ?? $"?{index}"))
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.Select(item => item.row).ToArray();
                var first = rows[0].Project;
                var latest = rows.OrderByDescending(r => r.Project.LastActiveAt).First().Project;
                var priced = rows.Where(r => r.Project.CreditsUsed.HasValue).ToArray();
                var costStatus = priced.Length == 0 ? ProjectCostStatus.Unknown
                    : missingRuntime || rows.Any(r => r.Project.CostStatus != ProjectCostStatus.Known)
                        ? ProjectCostStatus.Partial : ProjectCostStatus.Known;
                double? amount = priced.Length == 0 ? null
                    : UsageCredits.ToAmount(priced.Sum(r => r.Project.CreditsUsed!.Value), settings.AmountPerThousandCredits);
                var threads = rows.GroupBy(r => r.Runtime).ToDictionary(g => g.Key, g => g.Sum(r => r.Project.ThreadCount));
                return new CombinedProject(first.Id, first.Name, latest.Branch, rows.Sum(r => r.Project.Tokens),
                    amount, costStatus, priced.Any(r => r.Project.CostIsEstimated), latest.LastActiveAt, threads.Keys.ToArray(),
                    threads, rows.Max(r => r.Project.Quality));
            }).OrderByDescending(p => p.Tokens).ToArray();
    }

    private static string? ProjectKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim().Replace('/', '\\');
        bool unc = normalized.StartsWith(@"\\", StringComparison.Ordinal);
        if (!unc && !(normalized.Length >= 3 && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':' && normalized[2] == '\\')) return null;
        var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (unc && (parts.Length < 2 || parts[0] is "?" or ".")) return null;
        var key = (unc ? @"\\" : "") + string.Join('\\', parts);
        return (key + "\\").Contains(@"\.claude\projects\", StringComparison.OrdinalIgnoreCase) ? null : key;
    }

    private static IReadOnlyList<CombinedForecast> ForecastsFor(DashboardSnapshot[] snapshots)
    {
        var windows = snapshots.SelectMany(s => new[]
        {
            (s.Runtime, Weekly: false, Quota: s.PrimaryQuota, Forecast: s.PrimaryForecast),
            (s.Runtime, Weekly: true, Quota: s.SecondaryQuota, Forecast: s.SecondaryForecast)
        }).Where(w => w.Quota is not null && w.Quota.Source is not ("claude-desktop-history" or "claude-desktop-get-usage")).ToArray();
        var candidates = windows.Where(w => w.Forecast?.ExhaustsBeforeReset == true)
            .OrderBy(w => w.Forecast!.ExhaustsAt).ToList();
        var phases = new List<CombinedForecast>();
        while (candidates.Count > 0)
        {
            var first = candidates[0];
            var next = candidates.Min(w => w.Quota!.ResetsAt);
            phases.Add(new(first.Runtime, first.Weekly, first.Forecast!.ExhaustsAt, next, candidates.Count, windows.Length));
            if (next is null) break;
            candidates.RemoveAll(w => w.Quota!.ResetsAt == next);
        }
        return phases;
    }
}
