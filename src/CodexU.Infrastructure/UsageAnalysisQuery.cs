using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>All views share this selection; paging never changes aggregate denominators.</summary>
public static class UsageAnalysisQuery
{
    public const string UnknownProject = "__unknown__";

    public static UsageAnalysisResult Execute(UsageAnalysisData? data, UsageAnalysisRequest request,
        IReadOnlyList<string>? diagnostics = null)
    {
        Validate(request);
        var all = data?.Entries ?? [];
        var model = string.IsNullOrWhiteSpace(request.Model) ? null : UsageCredits.NormalizeModel(request.Model);
        var project = string.IsNullOrWhiteSpace(request.Project) ? null : request.Project;
        bool DateMatches(DateOnly date) => (request.From is null || date >= request.From)
            && (request.To is null || date <= request.To);
        var selected = all.Where(e => DateMatches(e.Date)
            && (model is null || string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase))
            && (project is null || string.Equals(e.Project ?? UnknownProject, project, StringComparison.OrdinalIgnoreCase))).ToArray();

        // Resolve lineage from the complete dataset, not the filtered subset. A
        // parent's model may be filtered out while its child's contribution remains.
        var parents = all.Where(e => e.SessionId is not null).GroupBy(e => e.SessionId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ParentSessionId).Distinct().Count() == 1
                ? g.First().ParentSessionId : null, StringComparer.Ordinal);
        string Root(string session)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { session };
            var current = session;
            while (parents.TryGetValue(current, out var parent) && parent is not null && parents.ContainsKey(parent))
            {
                // Corrupt/cyclic lineage must not invent ownership or lose records.
                if (!visited.Add(parent)) return session;
                current = parent;
            }
            return current;
        }

        var groups = selected.Where(e => e.SessionId is not null).GroupBy(e => Root(e.SessionId!), StringComparer.Ordinal)
            .OrderByDescending(g => g.Sum(e => e.Tokens)).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var page = Math.Min(request.Page, Math.Max(1, (groups.Length + request.PageSize - 1) / request.PageSize));
        var sessions = groups.Skip((page - 1) * request.PageSize).Take(request.PageSize).Select(group =>
        {
            var members = group.GroupBy(e => e.SessionId!, StringComparer.Ordinal).Select(member => new UsageSessionMember(
                member.Key, parents.GetValueOrDefault(member.Key), member.FirstOrDefault(e => e.Title is not null)?.Title,
                member.Select(e => e.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? member.First().Project : null,
                member.Min(e => e.Date), member.Max(e => e.Date), Total(member),
                member.Select(e => e.Source).Distinct().Order().ToArray(),
                member.OrderBy(e => e.Date).ThenBy(e => e.Model, StringComparer.Ordinal).ToArray()))
                .OrderBy(e => e.Id == group.Key ? 0 : 1).ThenByDescending(e => e.Totals.Tokens).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
            return new UsageSessionGroup(group.Key, all.FirstOrDefault(e => e.SessionId == group.Key && e.Title is not null)?.Title,
                group.Min(e => e.Date), group.Max(e => e.Date), Total(group), members);
        }).ToArray();

        var notes = (diagnostics ?? []).ToList();
        if (data is null) notes.Add("当前来源没有可下钻的会话用量数据。");
        if (project is not null || model is not null)
            notes.Add("仅计入能够确认匹配所选模型和项目的记录；无法归属的旧汇总不会分摊到具体模型或项目。");
        if (selected.Any(e => e.SessionId is null))
            notes.Add("未归属历史用量来自旧日汇总差额，没有足够证据还原会话、Token 分项或当前费率金额。");
        if (selected.Any(e => e.Source == "conflict"))
            notes.Add("来源发生不兼容改写，保留先前确认的历史用量并标记冲突。");
        notes.Add("输入已包含缓存读取及来源支持的缓存写入；推理 Token 已包含在输出中，不能再次相加。分项仅覆盖有明细的用量。");
        var estimates = model is null && project is null
            ? (data?.LegacyEstimates ?? []).Where(e => DateMatches(e.Date)).OrderBy(e => e.Date).ToArray() : [];
        return new(request.Runtime, request.From, request.To,
            all.Count == 0 ? null : all.Min(e => e.Date), all.Count == 0 ? null : all.Max(e => e.Date),
            Total(selected), selected.GroupBy(e => e.Date).OrderBy(g => g.Key).Select(g => new UsageAnalysisDay(g.Key, Total(g))).ToArray(),
            Groups(selected, e => e.Model, id => id == "unknown" ? "未知模型" : id),
            Groups(selected, e => e.Feature, id => id),
            Groups(selected, e => e.Project ?? UnknownProject, ProjectLabel),
            sessions, groups.Length, page, request.PageSize,
            selected.Where(e => e.SessionId is null).OrderBy(e => e.Date).ToArray(), estimates,
            all.Select(e => e.Model).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            all.Select(e => e.Project ?? UnknownProject).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(id => new UsageProjectOption(id, ProjectLabel(id))).ToArray(), notes.Distinct().ToArray(),
            selected.Where(e => e.Model is not ("unknown" or "legacy-unattributed") && e.Rate is null)
                .GroupBy(e => (e.Model, e.Date)).OrderBy(g => g.Key.Model).ThenBy(g => g.Key.Date)
                .Select(g => new UsageMissingRate(g.Key.Model, g.Key.Date, g.Sum(e => e.Tokens))).ToArray());
    }

    internal static void Validate(UsageAnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Runtime)) throw new ArgumentException("不支持的工具。", nameof(request));
        if (request.From > request.To) throw new ArgumentException("起始日期不能晚于结束日期。", nameof(request));
        if (request.Page < 1 || request.PageSize is < 1 or > 100)
            throw new ArgumentException("分页范围无效。", nameof(request));
        if (request.Model?.Length > 256 || request.Project?.Length > 32768)
            throw new ArgumentException("筛选条件过长。", nameof(request));
    }

    internal static UsageAnalysisTotals Total(IEnumerable<UsageAnalysisEntry> entries)
    {
        var rows = entries.ToArray();
        var rated = rows.Where(e => e.CreditsUsed is not null).ToArray();
        return new(rows.Sum(e => e.Tokens), rows.Aggregate(TokenBreakdown.Zero, (sum, row) => sum.Add(row.Breakdown ?? TokenBreakdown.Zero)),
            rows.Where(e => e.Breakdown is not null).Sum(e => e.Tokens),
            rated.Length > 0 ? rated.Sum(e => e.CreditsUsed!.Value) : null,
            rated.Sum(e => e.Tokens), rows.Where(e => e.CreditsUsed is null).Sum(e => e.Tokens),
            rows.Where(e => e.SessionId is null).Sum(e => e.Tokens),
            rows.Where(e => e.Breakdown is not null).Select(e => e.AvailableBreakdownFields ?? [])
                .Aggregate((IEnumerable<string>?)null, (common, fields) => common is null ? fields : common.Intersect(fields))?.ToArray() ?? []);
    }

    private static UsageAnalysisGroup[] Groups(IEnumerable<UsageAnalysisEntry> entries,
        Func<UsageAnalysisEntry, string> key, Func<string, string> label) => entries.GroupBy(key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new UsageAnalysisGroup(g.Key, label(g.Key), Total(g)))
        .OrderByDescending(g => g.Totals.Tokens).ThenBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string ProjectLabel(string id) => id == UnknownProject ? "未知项目" : Path.GetFileName(id.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : id;
}
