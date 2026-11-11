using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record AttributedUsage(string Source, string? Workspace, DateOnly Date, string Model, TokenBreakdown Tokens, int Events, string? Branch = null, string SourceKind = "live", string Feature = "unknown", string? ParentSessionId = null, string? Title = null, UsageBreakdownFields? AvailableFields = null);
internal sealed record UsageProjection(TokenSummary Tokens, IReadOnlyList<DailyUsage> Daily,
    IReadOnlyList<ModelUsage> Models, IReadOnlyList<ProjectUsage> Projects, UsageHistoryStatus History, UsageAnalysisData AnalysisData);

internal static class UsageHistoryProjection
{
    internal static void AddPricingDiagnostics(TokenPeriod period, List<string> diagnostics)
    {
        if (period.MissingRateTokens > 0)
            diagnostics.Add($"有 {period.MissingRateTokens:N0} tokens 的模型暂无适用费率，未计入点数");
        if (period.IncompleteBreakdownTokens > 0)
            diagnostics.Add($"有 {period.IncompleteBreakdownTokens:N0} tokens 缺少计价所需的用量分项，未计入点数");
    }

    internal static IReadOnlyList<UsageDistributionSlice> Distribution(IEnumerable<AttributedUsage> entries) => entries
        .GroupBy(e => (Model: e.Model.ToLowerInvariant(), e.Feature))
        .Select(g => new UsageDistributionSlice(g.Key.Model, g.Key.Feature, g.Sum(e => e.Tokens.VisibleTotalTokens)))
        .Where(e => e.Tokens > 0).OrderBy(e => e.Model).ThenBy(e => e.Feature).ToArray();

    private static IReadOnlyList<UsageDistributionSlice> LegacyDistribution(long total, IEnumerable<AttributedUsage> entries)
    {
        // Readers already reconstruct/deduplicate sources before projection. Only
        // unambiguous observations in this day and scope can explain a legacy
        // total; conflicting revisions must not supply historical attribution.
        var known = Distribution(entries.Where(e => e.SourceKind is "live" or "retained"));
        var assigned = known.Sum(e => e.Tokens);
        if (assigned > total) return [new("unknown", "unknown", total)];
        return known.Append(new UsageDistributionSlice("unknown", "unknown", total - assigned))
            .GroupBy(e => (e.Model, e.Feature))
            .Select(g => new UsageDistributionSlice(g.Key.Model, g.Key.Feature, g.Sum(e => e.Tokens)))
            .Where(e => e.Tokens > 0).OrderBy(e => e.Model).ThenBy(e => e.Feature).ToArray();
    }

    internal static async Task<UsageProjection> BuildAsync(AgentRuntime runtime, IReadOnlyList<AttributedUsage> entries,
        string root, string? workspace, DataQuality quality, IReadOnlyList<ModelCreditRate>? rates,
        bool completeCatalog, int retained, int conflicts, CancellationToken ct)
    {
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
        return Build(entries, old, workspace, quality, rates, completeCatalog, retained, conflicts);
    }

    internal static UsageProjection Build(IReadOnlyList<AttributedUsage> entries, IReadOnlyCollection<DailyUsageRecord> old,
        string? workspace, DataQuality quality, IReadOnlyList<ModelCreditRate>? rates, bool completeCatalog, int retained, int conflicts)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Recheck the projection boundary as well as the reader boundary. A
        // workspace-specific snapshot cannot be explained by another workspace
        // or a source whose workspace is unknown.
        var scoped = entries.Where(e => WorkspaceScope.Contains(workspace, e.Workspace)).ToArray();
        var measured = scoped.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.ToArray());
        var legacy = old.Where(day => !measured.TryGetValue(day.Date, out var rows)
            || day.Tokens.VisibleTotalTokens > rows.Sum(row => row.Tokens.VisibleTotalTokens)).ToDictionary(day => day.Date);
        var selected = scoped.Where(e => !legacy.ContainsKey(e.Date)).ToArray();
        var contributions = selected.Concat(scoped.Where(e => legacy.ContainsKey(e.Date) && e.SourceKind is "live" or "retained")).ToArray();
        var analysis = BuildAnalysis(contributions, legacy.Values, rates, completeCatalog);
        var daySources = selected.GroupBy(e => e.Date).ToDictionary(g => g.Key,
            g => g.Any(e => e.SourceKind == "conflict") ? "conflict" : g.Any(e => e.SourceKind == "retained") ? "retained" : "live");
        var dayRecords = analysis.Entries.GroupBy(e => e.Date).ToDictionary(group => group.Key, group => Price(group));

        TokenPeriod Price(IEnumerable<UsageAnalysisEntry> rows)
        {
            var data = rows.ToArray();
            // Legacy totals preserve the known daily breakdown. Their archived price
            // is a separate whole-day estimate; only verified detail contributes cost.
            var snapshots = data.Select(e => e.Date).Distinct().Where(legacy.ContainsKey).Select(d => legacy[d]).ToArray();
            var tokens = data.Where(e => !legacy.ContainsKey(e.Date))
                .Aggregate(TokenBreakdown.Zero, (sum, row) => sum.Add(row.Breakdown ?? TokenBreakdown.Zero));
            tokens = snapshots.Aggregate(tokens, (sum, day) => sum.Add(day.Tokens));
            var priced = UsageCredits.CalculateAppliedRates(data.Where(e => e.CreditsUsed.HasValue)
                .Select(e => (e.Model, e.Breakdown!, e.Rate)));
            return new(data.Sum(e => e.Tokens), tokens, priced.CreditsUsed, data.Where(e => !e.CreditsUsed.HasValue).Sum(e => e.Tokens),
                priced.ByModel, snapshots.Length > 0 ? DataQuality.Approximate : quality,
                data.Where(e => e.Rate is null && e.Model is not ("unknown" or "legacy-unattributed")).Sum(e => e.Tokens),
                data.Where(e => e.Rate is not null && !e.CreditsUsed.HasValue).Sum(e => e.Tokens));
        }
        TokenPeriod Period(Func<DateOnly, bool> predicate) => Price(analysis.Entries.Where(e => predicate(e.Date)));
        var eventCounts = contributions.GroupBy(e => UsageCredits.NormalizeModel(e.Model), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Events), StringComparer.OrdinalIgnoreCase);
        var projectBranches = contributions.GroupBy(e => e.Workspace ?? "未知项目", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Date).LastOrDefault(e => e.Branch is not null)?.Branch, StringComparer.OrdinalIgnoreCase);
        var models = analysis.Entries.GroupBy(e => e.Model, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModelUsage(g.Key, g.Sum(e => e.Tokens), eventCounts.GetValueOrDefault(g.Key))).ToList();
        var projects = analysis.Entries.GroupBy(e => e.Project ?? "未知项目", StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var priced = UsageAnalysisQuery.Total(group);
            return new ProjectUsage(group.Key, Path.GetFileName(group.Key), group.Key == "未知项目" ? null : group.Key,
                priced.Tokens, group.Where(e => e.SessionId is not null).Select(e => e.SessionId).Distinct().Count(),
                new DateTimeOffset(group.Max(e => e.Date).ToDateTime(TimeOnly.MinValue)), projectBranches.GetValueOrDefault(group.Key),
                priced.CreditsUsed,
                group.Any(e => e.Source == "legacy") ? DataQuality.Approximate : quality,
                UnratedTokens: priced.UnratedTokens);
        }).ToList();
        var days = Enumerable.Range(0, 182).Select(i => today.AddDays(i - 181)).Select(date =>
        {
            var day = dayRecords.GetValueOrDefault(date);
            return new DailyUsage(date, day?.Tokens ?? 0, day?.CreditsUsed ?? 0, day?.Quality ?? quality,
                legacy.ContainsKey(date) ? "legacy" : daySources.GetValueOrDefault(date, "live"),
                legacy.ContainsKey(date)
                    ? LegacyDistribution(legacy[date].Tokens.VisibleTotalTokens, measured.GetValueOrDefault(date) ?? [])
                    : Distribution(measured.GetValueOrDefault(date) ?? []), day?.UnratedTokens ?? 0);
        }).ToArray();
        return new(new(Period(d => d == today), Period(d => d >= today.AddDays(-6) && d <= today),
            Period(d => d.Year == today.Year && d.Month == today.Month && d <= today), Period(_ => true)), days,
            models.OrderByDescending(e => e.Tokens).ToArray(), projects.OrderByDescending(e => e.Tokens).ToArray(),
            new(retained, conflicts, legacy.Count), analysis);
    }

    internal static UsageAnalysisData BuildAnalysis(IEnumerable<AttributedUsage> entries,
        IEnumerable<DailyUsageRecord> legacy, IReadOnlyList<ModelCreditRate>? rates, bool completeCatalog)
    {
        var rows = entries.Where(e => e.Tokens.VisibleTotalTokens > 0).Select(e =>
        {
            var model = UsageCredits.NormalizeModel(e.Model);
            if (model == "claude-unknown") model = "unknown";
            var rate = model is "unknown" or "legacy-unattributed" ? null : UsageCredits.FindRate(e.Model, e.Date, rates, completeCatalog);
            var required = UsageBreakdownFields.Input | UsageBreakdownFields.Output;
            if (rate is not null && rate.InputCreditsPerMillion != rate.CachedInputCreditsPerMillion) required |= UsageBreakdownFields.CachedInput;
            if (e.Tokens.BillableCacheWriteTokens > 0) required |= UsageBreakdownFields.CacheWrite5m | UsageBreakdownFields.CacheWrite1h;
            var priceable = (e.AvailableFields.GetValueOrDefault() & required) == required;
            var amount = rate is null || !priceable ? (double?)null
                : UsageCredits.CalculateAppliedRates([(model, e.Tokens, rate)]).CreditsUsed;
            var explanation = !priceable ? "来源缺少核算所需的 Token 分项；保留总量，金额暂不可核算。" : null;
            if (rate is null && model is not ("unknown" or "legacy-unattributed"))
                explanation = "该模型在用量发生日期没有适用费率。" + explanation;
            return new UsageAnalysisEntry(e.Date, e.Source, e.ParentSessionId, e.Title, e.Workspace,
                model, e.Feature, e.Tokens.VisibleTotalTokens, e.Tokens, amount, rate, e.SourceKind,
                explanation,
                AvailableFieldNames(e.AvailableFields));
        }).ToList();
        var estimates = new List<UsageLegacyEstimate>();
        var assigned = rows.GroupBy(e => e.Date).ToDictionary(g => g.Key, g => g.Sum(e => e.Tokens));
        foreach (var day in legacy)
        {
            var remainder = Math.Max(0, day.Tokens.VisibleTotalTokens - assigned.GetValueOrDefault(day.Date));
            if (remainder > 0)
                rows.Add(new(day.Date, null, null, null, null, "unknown", "unknown", remainder, null, null, null, "legacy",
                    "旧日汇总高于同范围可确认的日志明细；差额缺少会话及分项证据，不能进一步归属。"));
            estimates.Add(new(day.Date, day.Tokens.VisibleTotalTokens, day.CreditsUsed,
                "旧版整日估值（包含当天已恢复明细），与当前费率重算金额存在重叠，仅供历史参考，不相加。"));
        }
        return new(rows.OrderBy(e => e.Date).ThenBy(e => e.SessionId, StringComparer.Ordinal).ThenBy(e => e.Model).ToArray(), estimates);
    }

    private static IReadOnlyList<string> AvailableFieldNames(UsageBreakdownFields? fields) => new[]
    {
        (UsageBreakdownFields.Input, "inputTokens"), (UsageBreakdownFields.CachedInput, "cachedInputTokens"),
        (UsageBreakdownFields.Output, "outputTokens"), (UsageBreakdownFields.ReasoningOutput, "reasoningOutputTokens"),
        (UsageBreakdownFields.CacheWrite5m, "cacheWrite5mTokens"), (UsageBreakdownFields.CacheWrite1h, "cacheWrite1hTokens")
    }.Where(pair => (fields.GetValueOrDefault() & pair.Item1) != 0).Select(pair => pair.Item2).ToArray();
}
