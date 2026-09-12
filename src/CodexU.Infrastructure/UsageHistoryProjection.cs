using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record AttributedUsage(string Source, string? Workspace, DateOnly Date, string Model, TokenBreakdown Tokens, int Events, string? Branch = null, string SourceKind = "live");
internal sealed record UsageProjection(TokenSummary Tokens, IReadOnlyList<DailyUsage> Daily,
    IReadOnlyList<ModelUsage> Models, IReadOnlyList<ProjectUsage> Projects, UsageHistoryStatus History);

internal static class UsageHistoryProjection
{
    internal static async Task<UsageProjection> BuildAsync(AgentRuntime runtime, IReadOnlyList<AttributedUsage> entries,
        string root, string? workspace, DataQuality quality, IReadOnlyList<ModelCreditRate>? rates,
        bool completeCatalog, int retained, int conflicts, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var scope = DailyUsageHistoryStore.ScopeFingerprint(workspace);
        var old = await new DailyUsageHistoryStore(root).LoadAsync(runtime, scope, ct);
        try
        {
            var imported = await new UsageHistoryLedger(root).MergeAsync($"legacy/{runtime}/{scope}",
                old.ToDictionary(day => day.Date.ToString("yyyy-MM-dd")), (_, _) => true, ct);
            old = imported.Sources.Values.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        { /* Source observations remain usable even if archival migration is unavailable. */ }
        var measured = entries.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.ToArray());
        var legacy = old.Where(day => !measured.TryGetValue(day.Date, out var rows)
            || day.Tokens.VisibleTotalTokens > rows.Sum(row => row.Tokens.VisibleTotalTokens)).ToDictionary(day => day.Date);
        var selected = entries.Where(e => !legacy.ContainsKey(e.Date)).ToArray();
        var daySources = selected.GroupBy(e => e.Date).ToDictionary(g => g.Key,
            g => g.Any(e => e.SourceKind == "conflict") ? "conflict" : g.Any(e => e.SourceKind == "retained") ? "retained" : "live");
        var dayRecords = new Dictionary<DateOnly, DailyUsageRecord>();
        foreach (var group in selected.GroupBy(e => e.Date))
        {
            var period = Price(group);
            dayRecords[group.Key] = new(group.Key, period.Breakdown, period.CreditsUsed, period.UnratedTokens, quality);
        }
        foreach (var day in legacy.Values) dayRecords[day.Date] = day with { Quality = DataQuality.Approximate };

        TokenPeriod Price(IEnumerable<AttributedUsage> rows)
        {
            var data = rows.ToArray();
            var tokens = data.Aggregate(TokenBreakdown.Zero, (sum, row) => sum.Add(row.Tokens));
            var price = UsageCredits.Calculate(data.Select(e => new DatedModelTokenUsage(e.Date, e.Model, e.Tokens)), rates, completeCatalog);
            return new(tokens.VisibleTotalTokens, tokens, price.CreditsUsed, price.UnratedTokens, price.ByModel, quality);
        }
        TokenPeriod Period(Func<DateOnly, bool> predicate)
        {
            var current = Price(selected.Where(e => predicate(e.Date)));
            var snapshots = legacy.Values.Where(e => predicate(e.Date)).ToArray();
            var tokens = snapshots.Aggregate(current.Breakdown, (sum, row) => sum.Add(row.Tokens));
            return current with
            {
                Tokens = tokens.VisibleTotalTokens,
                Breakdown = tokens,
                CreditsUsed = current.CreditsUsed + snapshots.Sum(e => e.CreditsUsed),
                UnratedTokens = current.UnratedTokens + snapshots.Sum(e => e.UnratedTokens),
                Quality = snapshots.Length > 0 ? DataQuality.Approximate : quality
            };
        }
        var models = selected.GroupBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelUsage(g.Key, g.Sum(e => e.Tokens.VisibleTotalTokens), g.Sum(e => e.Events))).ToList();
        if (legacy.Count > 0) models.Add(new("legacy-unattributed", legacy.Values.Sum(e => e.Tokens.VisibleTotalTokens), 0));
        var projects = selected.GroupBy(e => e.Workspace ?? "未知项目", StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var priced = Price(group);
            return new ProjectUsage(group.Key, Path.GetFileName(group.Key), group.Key == "未知项目" ? null : group.Key,
                priced.Tokens, group.Select(e => e.Source).Distinct().Count(),
                new DateTimeOffset(group.Max(e => e.Date).ToDateTime(TimeOnly.MinValue)), group.OrderBy(e => e.Date).LastOrDefault(e => e.Branch is not null)?.Branch,
                priced.CreditsUsed > 0 ? priced.CreditsUsed : null, quality);
        }).ToList();
        if (legacy.Count > 0) projects.Add(new("legacy", "旧版历史（项目未知）", null,
            legacy.Values.Sum(e => e.Tokens.VisibleTotalTokens), 0, null, null,
            legacy.Values.Sum(e => e.CreditsUsed), DataQuality.Approximate));
        var days = Enumerable.Range(0, 182).Select(i => today.AddDays(i - 181)).Select(date =>
        {
            var day = dayRecords.GetValueOrDefault(date);
            return new DailyUsage(date, day?.Tokens.VisibleTotalTokens ?? 0, day?.CreditsUsed ?? 0, day?.Quality ?? quality,
                legacy.ContainsKey(date) ? "legacy" : daySources.GetValueOrDefault(date, "live"));
        }).ToArray();
        return new(new(Period(d => d == today), Period(d => d >= today.AddDays(-6) && d <= today),
            Period(d => d.Year == today.Year && d.Month == today.Month && d <= today), Period(_ => true)), days,
            models.OrderByDescending(e => e.Tokens).ToArray(), projects.OrderByDescending(e => e.Tokens).ToArray(),
            new(retained, conflicts, legacy.Count));
    }
}
