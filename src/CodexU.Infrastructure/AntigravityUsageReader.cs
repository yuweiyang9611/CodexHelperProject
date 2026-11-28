using System.Globalization;
using System.Text.Json;
using CodexU.Core;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

/// <summary>
/// Read-only local usage reader for Antigravity CLI (<c>.gemini\antigravity-cli</c>)
/// and Antigravity IDE/Desktop (<c>.gemini\antigravity</c>).
///
/// Never writes, migrates, or locks source databases, never launches <c>agy</c>,
/// never reads credential files, and never accesses the network.
/// </summary>
public sealed class AntigravityUsageReader : ILocalUsageReader
{
    private const int CacheVersion = 1;
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<string> _rootDirectories;
    private readonly string? _defaultWorkspace;
    private readonly IReadOnlyList<ModelCreditRate>? _customRates;
    private readonly bool _completeRateCatalog;
    private readonly string? _applicationDataDirectory;
    private readonly bool _incrementalIndexEnabled;

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    public AntigravityUsageReader(
        CodexPaths paths,
        string? defaultWorkspace = null,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false,
        string? applicationDataDirectory = null,
        bool incrementalIndexEnabled = true)
        : this(
            DiscoverRoots(paths.HomeDirectory),
            defaultWorkspace,
            customRates,
            completeRateCatalog,
            applicationDataDirectory,
            incrementalIndexEnabled)
    {
    }

    internal AntigravityUsageReader(
        IReadOnlyList<string> rootDirectories,
        string? defaultWorkspace = null,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false,
        string? applicationDataDirectory = null,
        bool incrementalIndexEnabled = true)
    {
        _rootDirectories = rootDirectories;
        _defaultWorkspace = defaultWorkspace;
        _customRates = customRates;
        _completeRateCatalog = completeRateCatalog;
        _applicationDataDirectory = applicationDataDirectory;
        _incrementalIndexEnabled = incrementalIndexEnabled;
    }

    /// <summary>
    /// The CLI and the desktop app each keep their own data root. Hosts that drive agy
    /// over ACP (T3 Code) give it a private root with the same layout under
    /// <c>.t3\userdata\providers\antigravity\&lt;instance&gt;\antigravity-*</c>; runs made
    /// that way appear in no other root, so those roots are read too.
    /// </summary>
    internal static IReadOnlyList<string> DiscoverRoots(string homeDirectory)
    {
        List<string> roots =
        [
            Path.Combine(homeDirectory, ".gemini", "antigravity-cli"),
            Path.Combine(homeDirectory, ".gemini", "antigravity")
        ];
        var hosted = Path.Combine(homeDirectory, ".t3", "userdata", "providers", "antigravity");
        try
        {
            if (Directory.Exists(hosted))
            {
                roots.AddRange(Directory.EnumerateDirectories(hosted)
                    .SelectMany(instance => Directory.EnumerateDirectories(instance, "antigravity-*"))
                    .Order(StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable host root only hides those runs; the standard roots still count.
        }

        return roots;
    }

    public async Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var existingRoots = _rootDirectories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (existingRoots.Length == 0)
        {
            return Empty("Antigravity：未找到本机数据目录");
        }

        var cacheRoot = _applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        var indexPath = Path.Combine(cacheRoot, "antigravity-session-index-v1.json");
        var context = UsageReadContext.For(cacheRoot);
        var memory = context.Get("antigravity-index", () => new UsageReadContext.FileCache<Dictionary<string, ConversationCacheEntry>>());
        var cached = await LoadCacheAsync(indexPath, memory, diagnostics, cancellationToken);
        var nextCache = new Dictionary<string, ConversationCacheEntry>(StringComparer.OrdinalIgnoreCase);

        var observations = new List<CallObservation>();
        var skippedFiles = 0;
        var malformedRows = 0;
        var parsedFiles = 0;
        var reusedFiles = 0;
        var totalFiles = 0;

        foreach (var root in existingRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var summaries = await ReadSummariesAsync(root, diagnostics, () => skippedFiles++, cancellationToken);
            var conversationsDirectory = Path.Combine(root, "conversations");
            if (!Directory.Exists(conversationsDirectory)) continue;

            string[] dbFiles;
            try
            {
                dbFiles = Directory.EnumerateFiles(conversationsDirectory, "*.db", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skippedFiles++;
                diagnostics.Add($"无法枚举 Antigravity 对话目录：{e.Message}");
                continue;
            }

            totalFiles += dbFiles.Length;
            foreach (var dbFile in dbFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = Path.GetFileNameWithoutExtension(dbFile);
                if (string.IsNullOrWhiteSpace(conversationId)) continue;

                FileInfo info;
                string fileIdentity;
                string walStamp;
                try
                {
                    info = new FileInfo(dbFile);
                    if (!info.Exists) continue;
                    fileIdentity = SourceFileIdentity.Read(dbFile);
                    walStamp = ReadWalStamp(dbFile);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    skippedFiles++;
                    diagnostics.Add($"跳过 Antigravity 对话：{Path.GetFileName(dbFile)} · {e.Message}");
                    continue;
                }

                ConversationCalls? parsed;
                if (_incrementalIndexEnabled
                    && cached.TryGetValue(dbFile, out var entry)
                    && entry.Length == info.Length
                    && entry.ModifiedTicks == info.LastWriteTimeUtc.Ticks
                    && entry.FileIdentity == fileIdentity
                    && entry.WalStamp == walStamp)
                {
                    parsed = entry.Calls;
                    reusedFiles++;
                }
                else
                {
                    parsed = await ReadConversationDbAsync(dbFile, diagnostics, cancellationToken);
                    if (parsed is null)
                    {
                        skippedFiles++;
                        continue;
                    }

                    parsedFiles++;
                }

                nextCache[dbFile] = new(info.Length, info.LastWriteTimeUtc.Ticks, fileIdentity, walStamp, parsed);
                malformedRows += parsed.MalformedRows;

                // gen_metadata has no confirmed per-call timestamp field, so daily attribution
                // approximates using the conversation's last_modified_time (or .db file mtime).
                summaries.TryGetValue(conversationId, out var summary);
                var timestamp = summary?.LastModifiedTime ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                var workspace = summary?.Workspace;
                var parentId = summary?.ParentConversationId;

                foreach (var call in parsed.Calls)
                {
                    observations.Add(new(
                        conversationId,
                        parentId,
                        workspace,
                        timestamp,
                        call.Model,
                        call.ResponseId,
                        call.Tokens));
                }
            }
        }

        if (_incrementalIndexEnabled && (parsedFiles > 0 || nextCache.Count != cached.Count))
        {
            await SaveCacheAsync(cacheRoot, indexPath, nextCache, diagnostics, cancellationToken);
        }

        if (_incrementalIndexEnabled)
        {
            memory.Value = nextCache;
            memory.Stamp = UsageReadContext.Stamp(indexPath);
        }

        var deduplicated = DeduplicateByResponseId(observations);
        var attributed = new List<AttributedUsage>();
        var projectLastActive = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var usageEvents = 0;

        foreach (var item in deduplicated)
        {
            if (!WorkspaceScope.Contains(_defaultWorkspace, item.Workspace)) continue;
            usageEvents++;
            var date = DateOnly.FromDateTime(item.Timestamp.ToLocalTime().Date);
            var projectKey = item.Workspace ?? "未知项目";
            if (!projectLastActive.TryGetValue(projectKey, out var previous) || item.Timestamp > previous)
            {
                projectLastActive[projectKey] = item.Timestamp;
            }

            attributed.Add(new(
                item.ConversationId,
                item.Workspace,
                date,
                item.Model,
                item.Tokens,
                1,
                SourceKind: "live",
                Feature: item.ParentConversationId is null ? "tasks" : "subagents",
                ParentSessionId: item.ParentConversationId,
                AvailableFields: UsageBreakdownFields.Input | UsageBreakdownFields.CachedInput | UsageBreakdownFields.Output));
        }

        var quality = skippedFiles > 0 || malformedRows > 0
            ? DataQuality.Partial
            : usageEvents == 0 ? DataQuality.Unavailable : DataQuality.Detailed;

        var projection = UsageHistoryProjection.Build(
            attributed,
            [],
            _defaultWorkspace,
            quality,
            _customRates,
            _completeRateCatalog,
            retained: 0,
            conflicts: 0);

        var projects = projection.Projects
            .Select(project => projectLastActive.TryGetValue(project.Id, out var latest)
                ? project with { LastActiveAt = latest }
                : project)
            .ToArray();

        diagnostics.Add($"Antigravity 本地对话：已聚合 {reusedFiles + parsedFiles} 个对话、{usageEvents} 次模型调用");
        if (skippedFiles > 0)
            diagnostics.Add($"Antigravity 本地对话：{skippedFiles} 个数据库不可读或被占用，统计标记为部分数据");
        if (malformedRows > 0)
            diagnostics.Add($"Antigravity 本地对话：{malformedRows} 条元数据无法解析，已跳过");
        UsageHistoryProjection.AddPricingDiagnostics(projection.Tokens.Lifetime, diagnostics);

        var indexStatus = new IndexStatus(_incrementalIndexEnabled, reusedFiles, 0, parsedFiles, totalFiles, Clock.GetLocalNow());
        return new LocalUsageSnapshot(
            Account: null,
            PrimaryQuota: null,
            SecondaryQuota: null,
            projection.Tokens,
            Tasks: [],
            projection.Daily,
            projects,
            Tools: [],
            Skills: [],
            Sources: [new RankedUsage("antigravity-sqlite", "Antigravity SQLite", usageEvents, null, null, "Local")],
            projection.Models,
            Goals: [],
            TaskLifecycleStats.Empty,
            indexStatus,
            diagnostics.Distinct().ToArray(),
            projection.History,
            projection.AnalysisData);
    }

    private static IReadOnlyList<CallObservation> DeduplicateByResponseId(IReadOnlyList<CallObservation> observations)
    {
        var result = new List<CallObservation>(observations.Count);
        var byResponse = new Dictionary<(string ResponseId, string Model), int>();
        foreach (var item in observations)
        {
            if (item.ResponseId is not { Length: > 0 } responseId)
            {
                result.Add(item);
                continue;
            }

            var key = (responseId, item.Model.ToLowerInvariant());
            if (!byResponse.TryGetValue(key, out var existingIndex))
            {
                byResponse[key] = result.Count;
                result.Add(item);
                continue;
            }

            var existing = result[existingIndex];
            if (item.Tokens.VisibleTotalTokens > existing.Tokens.VisibleTotalTokens)
            {
                result[existingIndex] = existing with { Tokens = item.Tokens };
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, ConversationSummary>> ReadSummariesAsync(
        string root,
        List<string> diagnostics,
        Action onFailure,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ConversationSummary>(StringComparer.Ordinal);
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        if (!File.Exists(dbPath)) return result;

        try
        {
            await using var connection = OpenReadOnly(dbPath);
            await connection.OpenAsync(cancellationToken);

            var columns = await ReadColumnsAsync(connection, "conversation_summaries", cancellationToken);
            if (!columns.Contains("conversation_id"))
            {
                return result;
            }

            var hasWorkspaces = columns.Contains("workspace_uris");
            var hasModified = columns.Contains("last_modified_time");
            var hasParent = columns.Contains("parent_conversation_id");

            var sql = "SELECT conversation_id"
                + (hasWorkspaces ? ", workspace_uris" : ", NULL")
                + (hasModified ? ", last_modified_time" : ", NULL")
                + (hasParent ? ", parent_conversation_id" : ", NULL")
                + " FROM conversation_summaries";

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0)) continue;
                var id = reader.GetValue(0)?.ToString();
                if (string.IsNullOrWhiteSpace(id)) continue;

                var workspace = reader.IsDBNull(1) ? null : ParseFirstWorkspaceUri(reader.GetValue(1)?.ToString());
                var modified = reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetValue(2));
                var parent = reader.IsDBNull(3) ? null : reader.GetValue(3)?.ToString();
                if (string.IsNullOrWhiteSpace(parent)) parent = null;

                result[id] = new(workspace, modified, parent);
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            onFailure();
            diagnostics.Add($"读取 Antigravity 对话摘要失败：{Path.GetFileName(root)} · {e.Message}");
        }

        return result;
    }

    private static async Task<ConversationCalls?> ReadConversationDbAsync(
        string dbPath,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = OpenReadOnly(dbPath);
            await connection.OpenAsync(cancellationToken);

            var columns = await ReadColumnsAsync(connection, "gen_metadata", cancellationToken);
            if (!columns.Contains("data"))
            {
                return new ConversationCalls([], 0);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = columns.Contains("idx")
                ? "SELECT data FROM gen_metadata ORDER BY idx"
                : "SELECT data FROM gen_metadata";

            var calls = new List<CachedCall>();
            var malformed = 0;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0)) continue;
                byte[]? bytes;
                try
                {
                    bytes = reader.GetFieldValue<byte[]>(0);
                }
                catch (InvalidCastException)
                {
                    malformed++;
                    continue;
                }

                if (AntigravityGenMetadataParser.TryParse(bytes, out var parsed, out var isMalformed))
                {
                    calls.Add(new(parsed.Model, parsed.ResponseId, parsed.Tokens));
                }
                else if (isMalformed)
                {
                    malformed++;
                }
            }

            return new ConversationCalls(calls, malformed);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            diagnostics.Add($"跳过 Antigravity 对话：{Path.GetFileName(dbPath)} · {e.Message}");
            return null;
        }
    }

    private static SqliteConnection OpenReadOnly(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadOnly,
        DefaultTimeout = 2,
        Pooling = false
    }.ToString());

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    internal static string? ParseFirstWorkspaceUri(string? workspaceUrisJson)
    {
        if (string.IsNullOrWhiteSpace(workspaceUrisJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(workspaceUrisJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String) continue;
                var normalized = NormalizeWorkspaceUri(element.GetString());
                if (normalized is not null) return normalized;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    internal static string? NormalizeWorkspaceUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        var trimmed = uri.Trim();
        string rawPath;
        if (trimmed.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            rawPath = Uri.UnescapeDataString(trimmed[8..]).Replace('/', '\\');
            if (rawPath.Length >= 2 && char.IsAsciiLetter(rawPath[0]) && rawPath[1] == ':')
            {
                rawPath = char.ToUpperInvariant(rawPath[0]) + rawPath[1..];
            }
        }
        else if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            rawPath = @"\\" + Uri.UnescapeDataString(trimmed[7..]).Replace('/', '\\').TrimStart('\\');
        }
        else
        {
            return null;
        }

        var normalized = WorkspaceScope.Normalize(rawPath);
        if (normalized is { Length: >= 2 } && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            normalized = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        }

        return normalized;
    }

    internal static DateTimeOffset? ParseTimestamp(object? raw)
    {
        switch (raw)
        {
            case null or DBNull:
                return null;
            case string text when !string.IsNullOrWhiteSpace(text):
                if (DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                {
                    return parsed;
                }

                return long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericText)
                    ? FromUnixEpoch(numericText)
                    : null;
            case long unix:
                return FromUnixEpoch(unix);
            case int unixInt:
                return FromUnixEpoch(unixInt);
            case double unixDouble when double.IsFinite(unixDouble):
                return FromUnixEpoch((long)unixDouble);
            default:
                return null;
        }
    }

    private static DateTimeOffset? FromUnixEpoch(long value)
    {
        if (value <= 0) return null;
        try
        {
            if (value >= 1_000_000_000_000_000_000L) // nanoseconds
                return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000L);
            if (value >= 1_000_000_000_000_000L) // microseconds
                return DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000L);
            if (value >= 1_000_000_000_000L) // milliseconds
                return DateTimeOffset.FromUnixTimeMilliseconds(value);
            if (value >= 1_000_000_000L) // seconds
                return DateTimeOffset.FromUnixTimeSeconds(value);
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string ReadWalStamp(string dbFile)
    {
        var walPath = dbFile + "-wal";
        try
        {
            var wal = new FileInfo(walPath);
            return wal.Exists ? $"{wal.Length}:{wal.LastWriteTimeUtc.Ticks}" : "none";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    private async Task<Dictionary<string, ConversationCacheEntry>> LoadCacheAsync(
        string indexPath,
        UsageReadContext.FileCache<Dictionary<string, ConversationCacheEntry>> memory,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, ConversationCacheEntry>(StringComparer.OrdinalIgnoreCase);
        if (!_incrementalIndexEnabled) return empty;

        var stamp = UsageReadContext.Stamp(indexPath);
        if (memory.Stamp == stamp && memory.Value is not null)
        {
            return memory.Value;
        }

        if (!File.Exists(indexPath)) return empty;
        try
        {
            var index = JsonSerializer.Deserialize<ConversationIndex>(
                await File.ReadAllTextAsync(indexPath, cancellationToken), IndexJson);
            if (index is { Version: CacheVersion } && index.Entries is not null)
            {
                return new Dictionary<string, ConversationCacheEntry>(
                    index.Entries.Where(p => p.Value?.Calls?.Calls is not null),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add("Antigravity 索引不可用，重新解析本地对话");
        }

        return empty;
    }

    private static async Task SaveCacheAsync(
        string cacheRoot,
        string indexPath,
        Dictionary<string, ConversationCacheEntry> entries,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        var temp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheRoot);
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(new ConversationIndex(CacheVersion, entries), IndexJson),
                cancellationToken);
            File.Move(temp, indexPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Antigravity 索引写入失败：{e.Message}");
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static LocalUsageSnapshot Empty(string diagnostic)
    {
        var empty = DashboardSnapshot.Empty(AgentRuntime.Antigravity, diagnostic);
        return new LocalUsageSnapshot(
            null, null, null, empty.Tokens, empty.Tasks, empty.DailyUsage, empty.Projects,
            empty.Tools, empty.Skills, empty.Sources, empty.Models, empty.Goals,
            empty.TaskLifecycle, empty.IndexStatus, empty.Diagnostics);
    }

    private sealed record ConversationSummary(string? Workspace, DateTimeOffset? LastModifiedTime, string? ParentConversationId);

    private sealed record CachedCall(string Model, string? ResponseId, TokenBreakdown Tokens);

    private sealed record ConversationCalls(IReadOnlyList<CachedCall> Calls, int MalformedRows);

    private sealed record ConversationCacheEntry(
        long Length,
        long ModifiedTicks,
        string FileIdentity,
        string WalStamp,
        ConversationCalls Calls);

    private sealed record ConversationIndex(int Version, Dictionary<string, ConversationCacheEntry> Entries);

    private sealed record CallObservation(
        string ConversationId,
        string? ParentConversationId,
        string? Workspace,
        DateTimeOffset Timestamp,
        string Model,
        string? ResponseId,
        TokenBreakdown Tokens);
}
