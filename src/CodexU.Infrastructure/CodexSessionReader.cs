using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed record SessionAnalytics(
    AccountSnapshot? Account,
    RateLimitWindow? PrimaryQuota,
    RateLimitWindow? SecondaryQuota,
    TokenSummary Tokens,
    IReadOnlyList<DailyUsage> DailyUsage,
    IReadOnlyList<RankedUsage> Tools,
    IReadOnlyList<RankedUsage> Skills,
    IReadOnlyList<ModelUsage> Models,
    TaskLifecycleStats TaskLifecycle,
    IndexStatus IndexStatus,
    int ParsedFileCount,
    int TokenEventCount,
    int SkippedFileCount,
    IReadOnlyList<string> Diagnostics);

public sealed partial class CodexSessionReader(
    CodexPaths paths,
    bool incrementalIndexEnabled = true,
    string? indexDirectory = null,
    IReadOnlyList<ModelCreditRate>? customRates = null,
    bool completeRateCatalog = false)
{
    public async Task<SessionAnalytics> ReadAsync(CancellationToken cancellationToken = default)
    {
        string[] files;
        try
        {
            files = EnumerateSessionFiles().ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Empty($"无法枚举本机 session：{exception.Message}");
        }

        if (files.Length == 0)
        {
            return Empty("未找到精细 session token 事件");
        }

        var now = DateTimeOffset.Now;
        var todayDate = DateOnly.FromDateTime(now.Date);
        var sevenDayStart = todayDate.AddDays(-6);
        var monthStart = new DateOnly(now.Year, now.Month, 1);

        var today = new PeriodAccumulator();
        var sevenDays = new PeriodAccumulator();
        var month = new PeriodAccumulator();
        var lifetime = new PeriodAccumulator();
        var daily = new Dictionary<DateOnly, PeriodAccumulator>();
        var tools = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var parsedFiles = 0;
        var tokenEvents = 0;
        var taskStarted = 0;
        var taskCompleted = 0;
        var taskAborted = 0;
        long taskDuration = 0;
        long longestTaskDuration = 0;
        var diagnostics = new List<string>();
        SessionRateLimitSample? latestRateLimit = null;
        var reusedFiles = 0;
        var incrementalFiles = 0;
        var newlyParsedFiles = 0;
        var skippedFiles = 0;
        var skippedLines = 0;
        var indexCache = new SessionIndexCache(indexDirectory);
        var cachedEntries = incrementalIndexEnabled
            ? await indexCache.LoadAsync(cancellationToken)
            : new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var currentEntries = new List<SessionIndexEntry>(files.Length);
        var physicalFiles = new List<PhysicalSessionFile>(files.Length);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var fullPath = Path.GetFullPath(file);
                var fileInfo = new FileInfo(fullPath);
                ParsedSessionFile parsed;
                if (incrementalIndexEnabled
                    && cachedEntries.TryGetValue(fullPath, out var cached)
                    && cached.Matches(fileInfo))
                {
                    parsed = cached.Parsed;
                    reusedFiles++;
                }
                else if (incrementalIndexEnabled
                    && cachedEntries.TryGetValue(fullPath, out cached)
                    && cached.Length > 0
                    && cached.Length < fileInfo.Length)
                {
                    parsed = await ParseFileAsync(fullPath, cached.Parsed, cached.Length, cancellationToken);
                    incrementalFiles++;
                }
                else
                {
                    parsed = await ParseFileAsync(fullPath, cancellationToken);
                    newlyParsedFiles++;
                }

                if (incrementalIndexEnabled)
                {
                    fileInfo.Refresh();
                    currentEntries.Add(new SessionIndexEntry(
                        fullPath,
                        parsed.Offset,
                        fileInfo.LastWriteTimeUtc.Ticks,
                        parsed));
                }

                physicalFiles.Add(new PhysicalSessionFile(
                    fullPath,
                    fileInfo.LastWriteTimeUtc.Ticks,
                    parsed));
            }
            catch (IOException)
            {
                skippedFiles++;
                diagnostics.Add($"跳过正在变化的 session：{Path.GetFileName(file)}");
            }
            catch (UnauthorizedAccessException)
            {
                skippedFiles++;
                diagnostics.Add($"无权读取 session：{Path.GetFileName(file)}");
            }
        }

        if (incrementalIndexEnabled)
        {
            await indexCache.SaveAsync(currentEntries, cancellationToken);
            diagnostics.Add($"增量索引：复用 {reusedFiles} 个文件，续读 {incrementalFiles} 个文件，重新解析 {newlyParsedFiles} 个文件");
        }

        var reconstruction = ReconstructSessions(physicalFiles);
        parsedFiles = reconstruction.Files.Count;
        foreach (var resolved in reconstruction.Files)
        {
            var parsed = resolved.Source.Parsed;
            tokenEvents += parsed.TokenEventCount;
            skippedLines += parsed.SkippedLineCount;
            if (parsed.LatestRateLimit is { } candidate)
            {
                latestRateLimit = MergeRateLimitSamples(latestRateLimit, candidate);
            }

            taskStarted += parsed.TaskLifecycle.Started;
            taskCompleted += parsed.TaskLifecycle.Completed;
            taskAborted += parsed.TaskLifecycle.Aborted;
            taskDuration += parsed.TaskLifecycle.DurationMilliseconds;
            longestTaskDuration = Math.Max(longestTaskDuration, parsed.TaskLifecycle.LongestDurationMilliseconds);

            foreach (var tokenEvent in EffectiveTokenEvents(resolved))
            {
                var bucket = new SessionUsageBucket(tokenEvent.Date, tokenEvent.Model, tokenEvent.Tokens, 1);
                lifetime.Add(bucket);
                if (!daily.TryGetValue(bucket.Date, out var dailyPeriod))
                {
                    dailyPeriod = new PeriodAccumulator();
                    daily.Add(bucket.Date, dailyPeriod);
                }

                dailyPeriod.Add(bucket);
                if (bucket.Date >= monthStart && bucket.Date <= todayDate)
                {
                    month.Add(bucket);
                }

                if (bucket.Date >= sevenDayStart && bucket.Date <= todayDate)
                {
                    sevenDays.Add(bucket);
                }

                if (bucket.Date == todayDate)
                {
                    today.Add(bucket);
                }
            }

            MergeCounts(tools, parsed.Tools);
            MergeCounts(skills, parsed.Skills);
        }

        diagnostics.Add($"Sessions：已从 {physicalFiles.Count} 个文件重建 {parsedFiles} 个本机 session · {paths.SessionsDirectory}");
        if (reconstruction.DuplicateFileCount > 0)
        {
            diagnostics.Add($"Sessions：按 session ID 合并 {reconstruction.DuplicateFileCount} 个 active/archive 重复文件");
        }

        if (reconstruction.DivergentDuplicateCount > 0)
        {
            diagnostics.Add($"Sessions：{reconstruction.DivergentDuplicateCount} 组同 ID 文件内容分叉，已选较完整副本并标记为部分统计");
        }

        if (reconstruction.StructuralForkCount > 0 || reconstruction.PrefixForkCount > 0)
        {
            diagnostics.Add($"本机 fork 去重：结构边界 {reconstruction.StructuralForkCount} 个，父子公共前缀 {reconstruction.PrefixForkCount} 个");
        }

        if (reconstruction.AmbiguousForkCount > 0)
        {
            diagnostics.Add($"本机 fork 去重：{reconstruction.AmbiguousForkCount} 个子 session 缺少可验证父前缀，保守保留最多 {reconstruction.AmbiguousForkTokens:N0} tokens 并标记为部分统计");
        }

        if (latestRateLimit is not null)
        {
            diagnostics.Add("额度：已从最新 rollout rate_limits 恢复本机额度");
        }

        if (tokenEvents == 0)
        {
            diagnostics.Add("本机 session 中没有可识别的 token_count 事件");
        }

        if (skippedFiles > 0)
        {
            diagnostics.Add($"Sessions：有 {skippedFiles} 个文件未能读取，token 统计标记为部分数据");
        }

        if (skippedLines > 0)
        {
            diagnostics.Add($"Sessions：{skippedLines} 行无效或超过 4 MiB，已跳过且保留同文件内其余统计，数据标记为部分数据");
        }

        var tokenQuality = skippedFiles > 0
                           || skippedLines > 0
                           || reconstruction.AmbiguousForkCount > 0
                           || reconstruction.DivergentDuplicateCount > 0
            ? DataQuality.Partial
            : tokenEvents == 0 ? DataQuality.Unavailable : DataQuality.Detailed;
        var lifetimePeriod = lifetime.ToPeriod(tokenQuality, customRates, completeRateCatalog);
        if (reconstruction.AmbiguousForkTokens > 0)
        {
            var lowerBound = Math.Max(0, lifetimePeriod.Tokens - reconstruction.AmbiguousForkTokens);
            diagnostics.Add($"本机历史 Token 保守区间：{lowerBound:N0}–{lifetimePeriod.Tokens:N0}；主值保留未确认 fork，取区间上界");
        }

        if (lifetimePeriod.UnratedTokens > 0)
        {
            diagnostics.Add($"有 {lifetimePeriod.UnratedTokens:N0} tokens 的模型暂无点数费率，未计入点数");
        }

        var toolRanking = tools
            .OrderByDescending(pair => pair.Value)
            .Take(10)
            .Select(pair => new RankedUsage(pair.Key, FriendlyToolName(pair.Key), pair.Value, null, null, ToolCategory(pair.Key)))
            .ToArray();
        var skillRanking = skills
            .OrderByDescending(pair => pair.Value)
            .Take(10)
            .Select(pair => new RankedUsage(pair.Key, pair.Key, pair.Value, null, null, "Skill"))
            .ToArray();
        var models = lifetime.ModelUsages();
        var keptPrimary = QuotaFreshness.KeepCurrent(
            latestRateLimit?.Primary,
            latestRateLimit?.PrimaryTimestamp,
            now);
        var keptSecondary = QuotaFreshness.KeepCurrent(
            latestRateLimit?.Secondary,
            latestRateLimit?.SecondaryTimestamp,
            now);
        // Compared before the timestamp is stamped on: these are records, so a stamped
        // window would never equal the one it came from and the diagnostic would claim
        // a sample was dropped on every read.
        if (latestRateLimit is not null
            && (keptPrimary != latestRateLimit.Primary || keptSecondary != latestRateLimit.Secondary))
        {
            diagnostics.Add("额度：已忽略超过重置时间或样本窗口时长的本地额度样本");
        }

        // The rollout already records when each reading was taken, and the burn-rate
        // forecast divides consumption by elapsed time. Dropping the timestamp here
        // dated Codex readings by the refresh that happened to pick them up — so a
        // window read after the app was closed for hours looked like it had barely
        // moved, and the 5-hour warning that should have fired did not.
        var primaryQuota = keptPrimary is null
            ? null
            : keptPrimary with { MeasuredAt = latestRateLimit!.PrimaryTimestamp };
        var secondaryQuota = keptSecondary is null
            ? null
            : keptSecondary with { MeasuredAt = latestRateLimit!.SecondaryTimestamp };

        return new SessionAnalytics(
            latestRateLimit is null
                ? null
                : new AccountSnapshot("chatgpt", latestRateLimit.PlanType, null, true),
            primaryQuota,
            secondaryQuota,
            new TokenSummary(
                today.ToPeriod(tokenQuality, customRates, completeRateCatalog),
                sevenDays.ToPeriod(tokenQuality, customRates, completeRateCatalog),
                month.ToPeriod(tokenQuality, customRates, completeRateCatalog),
                lifetimePeriod),
            BuildDailyUsage(daily, now, tokenQuality, customRates, completeRateCatalog),
            toolRanking,
            skillRanking,
            models,
            new TaskLifecycleStats(taskStarted, taskCompleted, taskAborted, taskDuration, longestTaskDuration),
            new IndexStatus(incrementalIndexEnabled, reusedFiles, incrementalFiles, newlyParsedFiles, files.Length, DateTimeOffset.Now),
            parsedFiles,
            tokenEvents,
            skippedFiles,
            diagnostics);
    }

    private static IReadOnlyList<DailyUsage> BuildDailyUsage(
        Dictionary<DateOnly, PeriodAccumulator> source,
        DateTimeOffset now,
        DataQuality quality,
        IReadOnlyList<ModelCreditRate>? configuredRates,
        bool completeRateCatalog)
    {
        var end = DateOnly.FromDateTime(now.Date);
        var start = end.AddDays(-181);
        var result = new List<DailyUsage>(182);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (source.TryGetValue(date, out var accumulator))
            {
                var period = accumulator.ToPeriod(quality, configuredRates, completeRateCatalog);
                result.Add(new DailyUsage(date, period.Tokens, period.CreditsUsed, period.Quality));
            }
            else
            {
                result.Add(new DailyUsage(date, 0, 0, quality));
            }
        }

        return result;
    }

    private static void MergeCounts(Dictionary<string, int> target, IReadOnlyDictionary<string, int> source)
    {
        foreach (var pair in source)
        {
            target[pair.Key] = target.GetValueOrDefault(pair.Key) + pair.Value;
        }
    }

    private static string FriendlyToolName(string name) => name switch
    {
        "exec_command" or "shell_command" or "write_stdin" => "终端",
        "apply_patch" => "代码编辑",
        "web_search_call" or "search_query" or "open" or "click" => "浏览/检索",
        "update_plan" or "create_goal" or "update_goal" => "计划",
        _ => name
    };

    private static string ToolCategory(string name) => name switch
    {
        "exec_command" or "shell_command" or "write_stdin" => "Terminal",
        "apply_patch" => "Edit",
        "web_search_call" or "search_query" or "open" or "click" => "Web",
        "update_plan" or "create_goal" or "update_goal" => "Planning",
        _ when name.Contains("image", StringComparison.OrdinalIgnoreCase) => "Image",
        _ when name.Contains("mcp", StringComparison.OrdinalIgnoreCase) => "MCP",
        _ => "Other"
    };

    private static SessionAnalytics Empty(string diagnostic)
    {
        var empty = DashboardSnapshot.Empty(AgentRuntime.Codex, diagnostic);
        return new SessionAnalytics(
            null,
            null,
            null,
            empty.Tokens,
            empty.DailyUsage,
            [],
            [],
            [],
            TaskLifecycleStats.Empty,
            new IndexStatus(false, 0, 0, 0, 0, null),
            0,
            0,
            0,
            [diagnostic]);
    }

    private sealed class PeriodAccumulator
    {
        private TokenBreakdown _total = TokenBreakdown.Zero;
        private readonly Dictionary<string, TokenBreakdown> _byModel = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(DateOnly Date, string Model), TokenBreakdown> _datedByModel =
            new(UsageBucketKeyComparer.Instance);
        private readonly Dictionary<string, int> _modelEvents = new(StringComparer.OrdinalIgnoreCase);

        public void Add(SessionUsageBucket bucket)
        {
            _total = _total.Add(bucket.Tokens);
            _byModel[bucket.Model] = _byModel.GetValueOrDefault(bucket.Model, TokenBreakdown.Zero).Add(bucket.Tokens);
            var datedKey = (bucket.Date, bucket.Model);
            _datedByModel[datedKey] = _datedByModel
                .GetValueOrDefault(datedKey, TokenBreakdown.Zero)
                .Add(bucket.Tokens);
            _modelEvents[bucket.Model] = _modelEvents.GetValueOrDefault(bucket.Model) + bucket.EventCount;
        }

        public TokenPeriod ToPeriod(
            DataQuality quality,
            IReadOnlyList<ModelCreditRate>? configuredRates = null,
            bool completeRateCatalog = false)
        {
            var calculation = UsageCredits.Calculate(
                _datedByModel.Select(pair => new DatedModelTokenUsage(
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

        public IReadOnlyList<ModelUsage> ModelUsages() => _byModel
            .Select(pair => new ModelUsage(
                UsageCredits.NormalizeModel(pair.Key),
                pair.Value.VisibleTotalTokens,
                _modelEvents.GetValueOrDefault(pair.Key)))
            .GroupBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ModelUsage(group.Key, group.Sum(item => item.Tokens), group.Sum(item => item.EventCount)))
            .OrderByDescending(item => item.Tokens)
            .ToArray();
    }

}

public sealed record SessionUsageBucket(DateOnly Date, string Model, TokenBreakdown Tokens, int EventCount);

public sealed record ParsedSessionFile(
    IReadOnlyList<SessionUsageBucket> UsageBuckets,
    IReadOnlyDictionary<string, int> Tools,
    IReadOnlyDictionary<string, int> Skills,
    TaskLifecycleStats TaskLifecycle,
    int TokenEventCount,
    long Offset,
    TokenCounterState TokenCounterState,
    string CurrentModel,
    SessionRateLimitSample? LatestRateLimit,
    int SkippedLineCount,
    string? ForkReplayParentId,
    ForkReplayPhase ForkReplayPhase,
    string? SessionId,
    string? ForkedFromId,
    IReadOnlyList<SessionTokenEvent> TokenEvents);

public sealed record SessionTokenEvent(
    DateOnly Date,
    string Model,
    TokenBreakdown Tokens,
    SessionTokenEventFingerprint Identity,
    bool IsStructuralReplay);

public sealed record SessionTokenEventFingerprint(ulong First, ulong Second);

public enum ForkReplayPhase
{
    Uninspected,
    NotThreadSpawn,
    AwaitingParentMetadata,
    Replaying,
    Completed
}

internal sealed class UsageBucketKeyComparer : IEqualityComparer<(DateOnly Date, string Model)>
{
    public static UsageBucketKeyComparer Instance { get; } = new();

    public bool Equals((DateOnly Date, string Model) x, (DateOnly Date, string Model) y) =>
        x.Date == y.Date && string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((DateOnly Date, string Model) value) =>
        HashCode.Combine(value.Date, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model));
}

public sealed record SessionRateLimitSample(
    RateLimitWindow? Primary,
    DateTimeOffset? PrimaryTimestamp,
    RateLimitWindow? Secondary,
    DateTimeOffset? SecondaryTimestamp,
    string? PlanType,
    DateTimeOffset? PlanTimestamp);
