using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed partial class ClaudeCodeUsageReader(
    CodexPaths paths,
    string? defaultWorkspace = null,
    bool showSubagents = false,
    IReadOnlyList<ModelCreditRate>? customRates = null,
    bool completeRateCatalog = false,
    string? applicationDataDirectory = null,
    bool incrementalIndexEnabled = true) : ILocalUsageReader
{
    private const long MaximumTaskFileLength = 2 * 1024 * 1024;

    public async Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.Now;
        var todayDate = DateOnly.FromDateTime(now.Date);
        var sevenDayStart = todayDate.AddDays(-6);
        var lifetime = new PeriodAccumulator(customRates, completeRateCatalog);
        var tools = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parsedFiles = 0;
        var skippedFiles = 0;
        var skippedLines = 0;
        var usageEvents = 0;
        var assistantMessages = 0;
        var rateLimitHits = 0;
        var diagnostics = new List<string>();

        var attributed = new List<AttributedUsage>();
        var (sources, indexStatus, conflicts, retained, history) = await ReadSourcesAsync(diagnostics, cancellationToken);
        if (!string.IsNullOrWhiteSpace(defaultWorkspace) && sources.Values.Any(s => s.Events.Any(e => e.Workspace is null)))
            diagnostics.Add("工作区覆盖不足：部分 Claude 消息缺少项目归属，已从指定工作区统计排除");
        skippedFiles = conflicts;
        parsedFiles = sources.Count;
        foreach (var (sourceId, source) in sources)
        {
            skippedLines += source.SkippedLines;
            foreach (var item in source.Events)
            {
                if (!WorkspaceScope.Contains(defaultWorkspace, item.Workspace)) continue;
                assistantMessages++;
                var date = DateOnly.FromDateTime(item.Timestamp.ToLocalTime().Date);
                if (item.Throttled && date >= sevenDayStart && date <= todayDate) rateLimitHits++;
                foreach (var pair in item.Tools) tools[pair.Key] = tools.GetValueOrDefault(pair.Key) + pair.Value;
                foreach (var pair in item.Skills) skills[pair.Key] = skills.GetValueOrDefault(pair.Key) + pair.Value;
                if (item.Tokens is not { } tokens || item.Model is not { } model) continue;
                usageEvents++;
                var bucket = new UsageBucket(date, model, tokens);
                attributed.Add(new(sourceId, item.Workspace, date, model, tokens, 1, item.Branch, history?.Kind(sourceId) ?? "live"));
                lifetime.Add(bucket);

            }
        }

        var quality = skippedFiles > 0 || skippedLines > 0
            ? DataQuality.Partial
            : usageEvents == 0 ? DataQuality.Unavailable : DataQuality.Detailed;
        var lifetimePeriod = lifetime.ToPeriod(quality);
        if (lifetimePeriod.UnratedTokens > 0)
        {
            diagnostics.Add($"Claude Code 有 {lifetimePeriod.UnratedTokens:N0} tokens 尚未配置点数费率");
        }

        var (account, primary, secondary, quotaDiagnostic) = await ReadQuotaSnapshotAsync(cancellationToken);
        diagnostics.Add($"Claude transcripts：已聚合 {parsedFiles} 个文件、{assistantMessages} 条 assistant 消息、{usageEvents} 条 usage");
        diagnostics.Add(quotaDiagnostic);
        if (rateLimitHits > 0)
        {
            diagnostics.Add(
                $"Claude 额度：近 7 天有 {rateLimitHits} 次请求被限流（HTTP 429）。这是本机唯一的额度信号，"
                + "只说明当时已用满，无法据此推算余量。");
        }
        if (skippedFiles > 0)
        {
            diagnostics.Add($"Claude transcripts：{skippedFiles} 个文件读取失败，统计标记为部分数据");
        }
        if (skippedLines > 0)
        {
            diagnostics.Add("Claude transcript 包含无效行或超限行（单行超过 4 MiB），已跳过");
            diagnostics.Add($"Claude transcripts：{skippedLines} 行无效或过大，已跳过且保留同文件内其余统计");
        }

        var taskItems = ReadTasksSafely(showSubagents, diagnostics);
        var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.ClaudeCode, attributed,
            applicationDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU"),
            defaultWorkspace, quality, customRates, completeRateCatalog, retained, conflicts, cancellationToken);
        return new LocalUsageSnapshot(
            account,
            primary,
            secondary,
            projection.Tokens,
            taskItems,
            projection.Daily,
            projection.Projects,
            Rank(tools, ToolCategory),
            Rank(skills, _ => "Skill"),
            [new RankedUsage("claude-transcript", "Claude transcript", assistantMessages, null, null, "Local")],
            projection.Models,
            [],
            TaskLifecycleStats.Empty,
            indexStatus,
            diagnostics.Distinct().ToArray(),
            projection.History);
    }

    private static bool IsAssistantMessage(JsonElement root, out JsonElement message)
    {
        message = default;
        if (!string.Equals(ReadString(root, "type"), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        message = root.TryGetProperty("message", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;
        return true;
    }

    private static bool TryReadUsage(
        JsonElement root,
        JsonElement message,
        out string model,
        out TokenBreakdown tokens)
    {
        model = ReadString(message, "model") ?? ReadString(root, "model") ?? "claude-unknown";
        var usage = message.TryGetProperty("usage", out var messageUsage) && messageUsage.ValueKind == JsonValueKind.Object
            ? messageUsage
            : root.TryGetProperty("usage", out var rootUsage) && rootUsage.ValueKind == JsonValueKind.Object
                ? rootUsage
                : default;
        if (usage.ValueKind != JsonValueKind.Object)
        {
            tokens = TokenBreakdown.Zero;
            return false;
        }

        var directInput = ReadLong(usage, "input_tokens");
        var cacheCreation = ReadLong(usage, "cache_creation_input_tokens");
        var cacheRead = ReadLong(usage, "cache_read_input_tokens");
        var output = ReadLong(usage, "output_tokens");

        // cache_creation carries the per-TTL split. Writes bill above the base input
        // rate — 5 minute at 1.25x, 1 hour at 2x — so folding them into plain input
        // under-reports the cost. Fall back to the flat total when the nested object
        // is absent, and treat anything unaccounted for as the cheaper 5 minute tier
        // rather than inflating the estimate.
        var (cacheWrite5m, cacheWrite1h) = ReadCacheCreationSplit(usage, cacheCreation);

        var totalInput = Math.Max(0, directInput) + Math.Max(0, cacheCreation) + Math.Max(0, cacheRead);
        tokens = new TokenBreakdown(
            totalInput,
            Math.Max(0, cacheRead),
            Math.Max(0, output),
            0,
            totalInput + Math.Max(0, output),
            cacheWrite5m,
            cacheWrite1h);
        return tokens.VisibleTotalTokens > 0;
    }

    private static (long CacheWrite5m, long CacheWrite1h) ReadCacheCreationSplit(
        JsonElement usage,
        long cacheCreationTotal)
    {
        var total = Math.Max(0, cacheCreationTotal);
        if (!usage.TryGetProperty("cache_creation", out var creation)
            || creation.ValueKind != JsonValueKind.Object)
        {
            return (total, 0);
        }

        var oneHour = Math.Max(0, ReadLong(creation, "ephemeral_1h_input_tokens"));
        var fiveMinute = Math.Max(0, ReadLong(creation, "ephemeral_5m_input_tokens"));
        if (oneHour + fiveMinute <= 0)
        {
            return (total, 0);
        }

        // The flat counter is authoritative for the total; give any remainder to the
        // cheaper tier so a newly added TTL bucket cannot silently overstate cost.
        oneHour = Math.Min(oneHour, total);
        fiveMinute = Math.Max(0, total - oneHour);
        return (fiveMinute, oneHour);
    }

    private static void CountTools(
        JsonElement message,
        Dictionary<string, int> tools,
        Dictionary<string, int> skills)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in content.EnumerateArray())
        {
            if (!string.Equals(ReadString(item, "type"), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ReadString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            tools[name] = tools.GetValueOrDefault(name) + 1;
            if (!string.Equals(name, "Skill", StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("input", out var input)
                || input.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var skill = ReadString(input, "skill") ?? ReadString(input, "name");
            if (!string.IsNullOrWhiteSpace(skill))
            {
                skills[skill] = skills.GetValueOrDefault(skill) + 1;
            }
        }
    }

    private async Task<(AccountSnapshot? Account, RateLimitWindow? Primary, RateLimitWindow? Secondary, string Diagnostic)>
        ReadQuotaSnapshotAsync(CancellationToken cancellationToken)
    {
        var dataDirectory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        var candidates = new[]
        {
            Path.Combine(dataDirectory, "claude-code", "statusline-snapshot.json"),
            Path.Combine(paths.ClaudeDirectory, "statusline-snapshot.json")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            // Claude Code keeps no local record of remaining quota — transcripts carry
            // token counts but no allowance, and a 429 only shows up after the limit is
            // already hit. The one documented machine-readable source is the JSON piped
            // to a configured statusLine command, which tools/claude-statusline-snapshot.mjs
            // turns into this file. Point at that rather than calling the file optional.
            return (
                null,
                null,
                null,
                "Claude 额度：尚未接入额度数据。Claude Code 不在本机记录额度余量，需把 "
                + "tools/claude-statusline-snapshot.mjs 配置为 statusLine 命令，由它写入 "
                + $"{candidates[0]}。注意额度只对 Claude.ai 订阅账号可见，且要等会话内第一次 API 响应之后才出现。"
                + "未接入时额度显示为 --。");
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var first = ReadQuota(root, "primary") ?? ReadQuota(root, "fiveHour") ?? ReadQuota(root, "five_hour");
            var second = ReadQuota(root, "secondary") ?? ReadQuota(root, "sevenDay") ?? ReadQuota(root, "seven_day");
            var classified = RateLimitWindowClassifier.Classify(first, second);
            var plan = ReadString(root, "planType") ?? ReadString(root, "plan");
            var account = new AccountSnapshot("claude-code", plan, null, true, AgentRuntime.ClaudeCode);
            var sampledAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var now = DateTimeOffset.UtcNow;
            // The snapshot is written by Claude Code's statusLine command, so its write
            // time is when the figures were true — not when this refresh read them.
            var primary = QuotaFreshness.KeepCurrent(classified.FiveHour, sampledAt, now) is { } five
                ? five with { MeasuredAt = sampledAt }
                : null;
            var secondary = QuotaFreshness.KeepCurrent(classified.SevenDay, sampledAt, now) is { } seven
                ? seven with { MeasuredAt = sampledAt }
                : null;
            var staleCount = (classified.FiveHour is not null && primary is null ? 1 : 0)
                + (classified.SevenDay is not null && secondary is null ? 1 : 0);
            var freshnessDiagnostic = staleCount > 0
                ? $"；已忽略 {staleCount} 个过期额度窗口"
                : string.Empty;
            return (account, primary, secondary, $"Claude 额度：已读取 {path}{freshnessDiagnostic}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null, null, $"Claude 额度 snapshot 读取失败：{exception.Message}");
        }
    }

    private static RateLimitWindow? ReadQuota(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var used = ReadDouble(value, "usedPercent");
        if (used is null)
        {
            var remaining = ReadDouble(value, "remainingPercent");
            used = remaining is null ? null : 100d - remaining;
        }
        if (used is null) return null;
        var rawDuration = ReadLong(value, "windowDurationMinutes");
        int? duration = rawDuration > 0 && rawDuration <= int.MaxValue ? (int)rawDuration : null;
        DateTimeOffset? reset = DateTimeOffset.TryParse(ReadString(value, "resetsAt"), out var parsed) ? parsed : null;
        return new RateLimitWindow(Math.Clamp(used.Value, 0, 100), duration, reset);
    }

    private IReadOnlyList<TaskItem> ReadTasksSafely(bool includeSubagents, List<string> diagnostics)
    {
        var tasksDirectory = Path.Combine(paths.ClaudeDirectory, "tasks");
        if (!Directory.Exists(tasksDirectory))
        {
            diagnostics.Add("Claude tasks：未找到任务目录");
            return [];
        }

        var result = new List<TaskItem>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(tasksDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (new FileInfo(file).Length > MaximumTaskFileLength)
                    {
                        diagnostics.Add($"跳过 Claude task：{Path.GetFileName(file)} · 文件超过 2 MB");
                        continue;
                    }
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var item in EnumerateTaskObjects(document.RootElement))
                    {
                        var subagent = ReadBoolean(item, "isSubagent") || ReadBoolean(item, "subagent");
                        if (subagent && !includeSubagents) continue;
                        var title = ReadString(item, "subject") ?? ReadString(item, "title") ?? ReadString(item, "name");
                        if (string.IsNullOrWhiteSpace(title)) continue;
                        var status = ReadString(item, "status") ?? "pending";
                        var kind = status.ToLowerInvariant() switch
                        {
                            "completed" or "done" => TaskColumnKind.Done,
                            "in_progress" or "in-progress" or "active" => TaskColumnKind.Active,
                            "scheduled" => TaskColumnKind.Scheduled,
                            _ => TaskColumnKind.Pending
                        };
                        result.Add(new TaskItem(
                            ReadString(item, "id") ?? $"claude-{result.Count + 1}",
                            title.Trim(),
                            "Claude Code",
                            ReadTimestamp(item),
                            null,
                            kind,
                            subagent ? "子代理任务" : null));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    diagnostics.Add($"跳过 Claude task：{Path.GetFileName(file)} · {exception.Message}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Claude tasks 枚举失败：{exception.Message}");
        }

        diagnostics.Add($"Claude tasks：已读取 {result.Count} 项");
        return result.OrderByDescending(item => item.UpdatedAt).Take(100).ToArray();
    }

    private static IEnumerable<JsonElement> EnumerateTaskObjects(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
            yield break;
        }
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in tasks.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
        else
        {
            yield return root;
        }
    }

    private static RankedUsage[] Rank(Dictionary<string, int> source, Func<string, string> category) => source
        .OrderByDescending(pair => pair.Value)
        .Take(10)
        .Select(pair => new RankedUsage(pair.Key, pair.Key, pair.Value, null, null, category(pair.Key)))
        .ToArray();

    private static string ToolCategory(string name) => name.ToLowerInvariant() switch
    {
        "bash" or "shell" => "Terminal",
        "edit" or "write" or "multiedit" => "Edit",
        "websearch" or "webfetch" => "Web",
        "skill" => "Skill",
        _ => "Other"
    };

    private static DateTimeOffset? ReadTimestamp(JsonElement value)
    {
        foreach (var name in new[] { "timestamp", "updatedAt", "updated_at", "createdAt", "created_at" })
        {
            if (DateTimeOffset.TryParse(ReadString(value, name), out var parsed)) return parsed;
        }
        return null;
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long ReadLong(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetInt64(out var result)
            ? result
            : 0;

    private static double? ReadDouble(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.TryGetDouble(out var result)
            ? result
            : null;

    private static bool ReadBoolean(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.True;

    private static LocalUsageSnapshot Empty(string diagnostic)
    {
        var empty = DashboardSnapshot.Empty(AgentRuntime.ClaudeCode, diagnostic);
        return new LocalUsageSnapshot(
            null, null, null, empty.Tokens, empty.Tasks, empty.DailyUsage, empty.Projects,
            empty.Tools, empty.Skills, empty.Sources, empty.Models, empty.Goals,
            empty.TaskLifecycle, empty.IndexStatus, empty.Diagnostics);
    }

    private sealed record UsageBucket(DateOnly Date, string Model, TokenBreakdown Tokens);

    private sealed class PeriodAccumulator(
        IReadOnlyList<ModelCreditRate>? configuredRates,
        bool completeRateCatalog)
    {
        private TokenBreakdown _total = TokenBreakdown.Zero;
        private readonly Dictionary<(DateOnly Date, string Model), TokenBreakdown> _models =
            new(UsageBucketKeyComparer.Instance);

        public void Add(UsageBucket bucket)
        {
            _total = _total.Add(bucket.Tokens);
            // Group on the normalized id so an alias and its dated snapshot — the same
            // model at the same price — collapse into one row instead of splitting the
            // per-model breakdown in two when a session spans a model release.
            var key = (bucket.Date, UsageCredits.NormalizeModel(bucket.Model));
            _models[key] = _models.GetValueOrDefault(key, TokenBreakdown.Zero).Add(bucket.Tokens);
        }

        public TokenPeriod ToPeriod(DataQuality quality)
        {
            var calculation = UsageCredits.Calculate(
                _models.Select(pair => new DatedModelTokenUsage(
                    pair.Key.Date,
                    pair.Key.Model,
                    pair.Value)),
                configuredRates,
                completeRateCatalog);
            return new TokenPeriod(
                _total.VisibleTotalTokens,
                _total,
                calculation.CreditsUsed,
                calculation.UnratedTokens,
                calculation.ByModel,
                quality);
        }
    }

}
