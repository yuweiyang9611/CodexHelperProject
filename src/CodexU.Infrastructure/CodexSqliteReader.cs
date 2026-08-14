using CodexU.Core;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

public sealed class CodexSqliteReader(
    CodexPaths paths,
    string? defaultWorkspace = null,
    bool showSubagents = false)
{
    public async Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.StateDatabase))
        {
            return Empty("未找到本机 Codex 状态数据库");
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = paths.StateDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 2,
                Pooling = false
            };

            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);

            var columns = await ReadColumnsAsync(connection, cancellationToken);
            if (!columns.Contains("id") || !columns.Contains("tokens_used"))
            {
                return Empty("Codex 数据库缺少可识别的 threads 字段");
            }

            var threads = await ReadThreadsAsync(connection, columns, cancellationToken);
            var snapshot = Aggregate(threads);
            return snapshot with
            {
                Diagnostics = snapshot.Diagnostics
                    .Append($"SQLite：已读取 {threads.Count} 个线程 · {paths.StateDatabase}")
                    .ToArray()
            };
        }
        catch (SqliteException ex)
        {
            return Empty($"读取 Codex SQLite 失败：{ex.SqliteErrorCode}");
        }
        catch (IOException ex)
        {
            return Empty($"读取 Codex 数据库失败：{ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return Empty("无权读取 Codex 状态数据库");
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return Empty($"Codex 数据库包含无法识别的字段值：{ex.Message}");
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(threads);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }

    private static async Task<List<ThreadRow>> ReadThreadsAsync(
        SqliteConnection connection,
        HashSet<string> columns,
        CancellationToken cancellationToken)
    {
        // `preview` may contain user-authored prompt text. It is deliberately not
        // selected or displayed; a missing explicit title stays anonymous.
        var titleExpression = columns.Contains("title")
            ? "COALESCE(NULLIF(title, ''), '未命名任务')"
            : "'未命名任务'";
        var cwdExpression = columns.Contains("cwd") ? "COALESCE(cwd, '')" : "''";
        var updatedExpression = columns.Contains("updated_at") ? "updated_at" : "0";
        var archivedExpression = columns.Contains("archived_at") ? "archived_at" : "NULL";
        var statusExpression = columns.Contains("status") ? "COALESCE(status, '')" : "''";
        var modelExpression = columns.Contains("model") ? "COALESCE(model, '')" : "''";
        var sourceExpression = columns.Contains("thread_source") ? "COALESCE(thread_source, '')" : "''";
        var branchExpression = columns.Contains("git_branch") ? "COALESCE(git_branch, '')" : "''";

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id,
                   {titleExpression} AS display_title,
                   {cwdExpression} AS cwd,
                   COALESCE(tokens_used, 0) AS tokens_used,
                   {updatedExpression} AS updated_at,
                   {archivedExpression} AS archived_at,
                   {statusExpression} AS status,
                   {modelExpression} AS model,
                   {sourceExpression} AS thread_source,
                   {branchExpression} AS git_branch
            FROM threads
            ORDER BY updated_at DESC;
            """;

        var rows = new List<ThreadRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.IsDBNull(0) ? Guid.NewGuid().ToString("N") : Convert.ToString(reader.GetValue(0))!;
            var title = reader.IsDBNull(1) ? "未命名任务" : reader.GetString(1);
            var cwd = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var tokens = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3));
            var updated = reader.IsDBNull(4) ? null : UsageCredits.FromUnixTime(Convert.ToInt64(reader.GetValue(4)));
            var archived = reader.IsDBNull(5) ? null : UsageCredits.FromUnixTime(Convert.ToInt64(reader.GetValue(5)));
            var status = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var model = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var threadSource = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            var branch = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
            rows.Add(new ThreadRow(id, title, cwd, tokens, updated, archived, status, model, threadSource, branch));
        }

        return rows;
    }

    private LocalUsageSnapshot Aggregate(IReadOnlyList<ThreadRow> threads)
    {
        var now = DateTimeOffset.Now;
        var today = now.Date;
        var sevenDayStart = today.AddDays(-6);
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

        long lifetimeTokens = 0;
        long todayTokens = 0;
        long sevenDayTokens = 0;
        long monthTokens = 0;
        var daily = new Dictionary<DateOnly, long>();
        var projects = new Dictionary<string, ProjectAccumulator>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<TaskItem>();
        var models = new Dictionary<string, (long Tokens, int Threads)>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var thread in threads)
        {
            var tokens = Math.Max(0, thread.Tokens);
            lifetimeTokens += tokens;
            var modelName = UsageCredits.NormalizeModel(thread.Model);
            var modelValue = models.GetValueOrDefault(modelName);
            models[modelName] = (modelValue.Tokens + tokens, modelValue.Threads + 1);
            var sourceName = FriendlySource(thread.ThreadSource);
            sources[sourceName] = sources.GetValueOrDefault(sourceName) + 1;

            if (thread.UpdatedAt is { } updated)
            {
                var localUpdated = updated.ToLocalTime();
                var updatedDate = localUpdated.Date;
                var dateOnly = DateOnly.FromDateTime(updatedDate);
                daily[dateOnly] = daily.GetValueOrDefault(dateOnly) + tokens;

                if (updatedDate == today)
                {
                    todayTokens += tokens;
                }

                if (updatedDate >= sevenDayStart && updatedDate <= today)
                {
                    sevenDayTokens += tokens;
                }

                if (localUpdated >= monthStart && localUpdated <= now)
                {
                    monthTokens += tokens;
                }
            }

            var path = thread.Cwd.Trim();
            var projectKey = string.IsNullOrWhiteSpace(path) ? "未归类" : path;
            if (!projects.TryGetValue(projectKey, out var project))
            {
                project = new ProjectAccumulator(
                    projectKey,
                    string.IsNullOrWhiteSpace(path) ? "未归类" : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                    string.IsNullOrWhiteSpace(path) ? null : path);
                projects.Add(projectKey, project);
            }

            project.Add(tokens, thread.UpdatedAt, thread.Branch);

            if (ShouldDisplayTask(thread, now)
                && MatchesWorkspace(thread.Cwd)
                && (showSubagents || !IsSubagent(thread.ThreadSource)))
            {
                var kind = ClassifyTask(thread, now);
                tasks.Add(new TaskItem(
                    thread.Id,
                    thread.Title,
                    project.Name,
                    thread.ArchivedAt ?? thread.UpdatedAt,
                    tokens,
                    kind,
                    string.IsNullOrWhiteSpace(thread.Status) ? null : thread.Status));
            }
        }

        var dailyUsage = BuildDailyUsage(daily, now);
        var projectUsage = projects.Values
            .Select(value => value.ToUsage())
            .OrderByDescending(value => value.Tokens)
            .Take(12)
            .ToArray();

        var tokenSummary = new TokenSummary(
            CreateApproximatePeriod(todayTokens),
            CreateApproximatePeriod(sevenDayTokens),
            CreateApproximatePeriod(monthTokens),
            CreateApproximatePeriod(lifetimeTokens));

        return new LocalUsageSnapshot(
            null,
            null,
            null,
            tokenSummary,
            tasks.OrderByDescending(task => task.UpdatedAt).Take(24).ToArray(),
            dailyUsage,
            projectUsage,
            [],
            [],
            sources.Select(pair => new RankedUsage(pair.Key, pair.Key, pair.Value, null, null, "Source"))
                .OrderByDescending(item => item.Count)
                .ToArray(),
            models.Select(pair => new ModelUsage(pair.Key, pair.Value.Tokens, pair.Value.Threads))
                .OrderByDescending(item => item.Tokens)
                .Take(12)
                .ToArray(),
            [],
            TaskLifecycleStats.Empty,
            new IndexStatus(false, 0, 0, 0, 0, null),
            ["线程 token 按最近更新时间归因，属于近似口径"]);
    }

    private static TokenPeriod CreateApproximatePeriod(long tokens) =>
        new(
            tokens,
            new TokenBreakdown(0, 0, 0, 0, tokens),
            0,
            tokens,
            [],
            DataQuality.Approximate);

    private static IReadOnlyList<DailyUsage> BuildDailyUsage(Dictionary<DateOnly, long> source, DateTimeOffset now)
    {
        var end = DateOnly.FromDateTime(now.Date);
        var start = end.AddDays(-181);
        var result = new List<DailyUsage>(182);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var tokens = source.GetValueOrDefault(date);
            result.Add(new DailyUsage(date, tokens, 0, DataQuality.Approximate));
        }

        return result;
    }

    private static bool ShouldDisplayTask(ThreadRow thread, DateTimeOffset now)
    {
        var cutoff = now.Date.AddDays(-1);
        return thread.ArchivedAt?.ToLocalTime() >= cutoff || thread.UpdatedAt?.ToLocalTime() >= cutoff;
    }

    private bool MatchesWorkspace(string cwd)
    {
        if (string.IsNullOrWhiteSpace(defaultWorkspace))
        {
            return true;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(cwd))
            {
                return false;
            }

            var workspacePath = Path.GetFullPath(defaultWorkspace);
            var candidatePath = Path.GetFullPath(cwd);
            var relative = Path.GetRelativePath(workspacePath, candidatePath);
            return string.Equals(relative, ".", StringComparison.Ordinal)
                || (!Path.IsPathRooted(relative)
                    && !string.Equals(relative, "..", StringComparison.Ordinal)
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSubagent(string source) =>
        source.Contains("subagent", StringComparison.OrdinalIgnoreCase)
        || source.Contains("sub-agent", StringComparison.OrdinalIgnoreCase);

    private static string FriendlySource(string source) => source.ToLowerInvariant() switch
    {
        var value when value.Contains("subagent") || value.Contains("sub-agent") => "子代理",
        var value when value.Contains("automation") => "自动化",
        var value when value.Contains("review") => "审查",
        _ => "主任务"
    };

    private static TaskColumnKind ClassifyTask(ThreadRow thread, DateTimeOffset now)
    {
        if (thread.ArchivedAt is not null || thread.Status.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            return TaskColumnKind.Done;
        }

        if (thread.Status.Contains("active", StringComparison.OrdinalIgnoreCase)
            || thread.Status.Contains("progress", StringComparison.OrdinalIgnoreCase)
            || thread.UpdatedAt >= now.AddMinutes(-10))
        {
            return TaskColumnKind.Active;
        }

        return TaskColumnKind.Pending;
    }

    private static LocalUsageSnapshot Empty(string diagnostic)
    {
        var empty = DashboardSnapshot.Empty(AgentRuntime.Codex, diagnostic);
        return new LocalUsageSnapshot(
            null,
            null,
            null,
            empty.Tokens,
            empty.Tasks,
            empty.DailyUsage,
            empty.Projects,
            empty.Tools,
            empty.Skills,
            empty.Sources,
            empty.Models,
            empty.Goals,
            empty.TaskLifecycle,
            empty.IndexStatus,
            empty.Diagnostics);
    }

    private sealed record ThreadRow(
        string Id,
        string Title,
        string Cwd,
        long Tokens,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? ArchivedAt,
        string Status,
        string Model,
        string ThreadSource,
        string Branch);

    private sealed class ProjectAccumulator(string id, string name, string? fullPath)
    {
        private long _tokens;
        private int _threadCount;
        private DateTimeOffset? _lastActiveAt;
        private string? _branch;

        public string Name { get; } = string.IsNullOrWhiteSpace(name) ? "未归类" : name;

        public void Add(long tokens, DateTimeOffset? updatedAt, string? branch)
        {
            _tokens += tokens;
            _threadCount++;
            if (updatedAt is not null && (_lastActiveAt is null || updatedAt > _lastActiveAt))
            {
                _lastActiveAt = updatedAt;
                _branch = string.IsNullOrWhiteSpace(branch) ? _branch : branch;
            }
        }

        public ProjectUsage ToUsage() => new(
            id,
            Name,
            fullPath,
            _tokens,
            _threadCount,
            _lastActiveAt,
            _branch,
            null,
            DataQuality.Approximate);
    }
}
