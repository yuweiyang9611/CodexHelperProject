using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record AttributedUsage(string Source, string? Workspace, DateOnly Date, string Model, TokenBreakdown Tokens, int Events, string? Branch = null, string SourceKind = "live", string Feature = "unknown", string? ParentSessionId = null, string? Title = null, UsageBreakdownFields? AvailableFields = null);
internal sealed record UsageProjection(TokenSummary Tokens, IReadOnlyList<DailyUsage> Daily,
    IReadOnlyList<ModelUsage> Models, IReadOnlyList<ProjectUsage> Projects, UsageHistoryStatus History, UsageAnalysisData AnalysisData);

internal static class UsageHistoryProjection
{
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
        var eventCounts = contributions.GroupBy(e => UsageCredits.NormalizeModel(e.Model), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Events), StringComparer.OrdinalIgnoreCase);
        var compatibleProjectPrices = contributions.GroupBy(e => e.Workspace ?? "未知项目", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Price(g), StringComparer.OrdinalIgnoreCase);
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
                compatibleProjectPrices.TryGetValue(group.Key, out var compatible) && compatible.CreditsByModel.Count > 0 ? compatible.CreditsUsed : null,
                group.Any(e => e.Source == "legacy") ? DataQuality.Approximate : quality);
        }).ToList();
        var days = Enumerable.Range(0, 182).Select(i => today.AddDays(i - 181)).Select(date =>
        {
            var day = dayRecords.GetValueOrDefault(date);
            return new DailyUsage(date, day?.Tokens.VisibleTotalTokens ?? 0, day?.CreditsUsed ?? 0, day?.Quality ?? quality,
                legacy.ContainsKey(date) ? "legacy" : daySources.GetValueOrDefault(date, "live"),
                legacy.ContainsKey(date)
                    ? LegacyDistribution(day!.Tokens.VisibleTotalTokens, measured.GetValueOrDefault(date) ?? [])
                    : Distribution(measured.GetValueOrDefault(date) ?? []));
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
                : UsageCredits.Calculate([new DatedModelTokenUsage(e.Date, e.Model, e.Tokens)], rates, completeCatalog).CreditsUsed;
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
