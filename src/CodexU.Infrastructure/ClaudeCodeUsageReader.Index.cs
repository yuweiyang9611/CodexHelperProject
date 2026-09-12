using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed partial class ClaudeCodeUsageReader
{
    internal sealed record ReadMetrics(long TranscriptBytes, int ParsedLines, double ElapsedMilliseconds, long AllocatedBytes);
    internal Action<ReadMetrics>? MetricsObserved { get; init; }
    private sealed record ClaudeEvent(string? Workspace, DateTimeOffset Timestamp, string? Branch,
        string? Model, TokenBreakdown? Tokens, bool Throttled,
        IReadOnlyDictionary<string, int> Tools, IReadOnlyDictionary<string, int> Skills);
    private sealed record ClaudeSource(IReadOnlyList<ClaudeEvent> Events, int SkippedLines, long Offset, string? Identity = null);
    private sealed record ClaudeCacheEntry(long Length, long Modified, string Boundary, ClaudeSource Source, string FileIdentity);
    private sealed record ClaudeIndex(int Version, string Zone, Dictionary<string, ClaudeCacheEntry> Entries);
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web);

    private static async Task<ClaudeSource> ParseTranscriptAsync(string file, ClaudeSource? seed, Action<long, int> measured, CancellationToken ct)
    {
        var events = seed?.Events.ToList() ?? [];
        var skipped = seed?.SkippedLines ?? 0;
        var offset = seed?.Offset ?? 0;
        var complete = offset;
        var lines = 0;
        var identity = seed?.Identity;
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 32768, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = offset;
        var reader = new BoundedLineReader(stream);
        while (true)
        {
            var line = await reader.ReadAsync(ct);
            if (line.IsEndOfStream || !line.IsTerminated) break;
            lines++;
            complete = offset + reader.ConsumedBytes;
            if (line.IsTooLong) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(line.Line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line.Line);
                var root = document.RootElement;
                if (identity is null && ReadString(root, "sessionId") is { Length: > 0 and <= 128 } sessionId)
                {
                    var agent = ReadString(root, "agentId");
                    if (agent is null && string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "subagents", StringComparison.OrdinalIgnoreCase))
                        agent = Path.GetFileNameWithoutExtension(file);
                    identity = "session:" + sessionId + (agent is null ? "" : "/agent:" + agent);
                }
                if (!IsAssistantMessage(root, out var message)) continue;
                var tools = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                CountTools(message, tools, skills);
                var hasUsage = TryReadUsage(root, message, out var model, out var tokens);
                events.Add(new(WorkspaceScope.Normalize(ReadString(root, "cwd") ?? ReadString(root, "projectPath")),
                    ReadTimestamp(root) ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file)), ReadString(root, "gitBranch"),
                    hasUsage ? model : null, hasUsage ? tokens : null, ReadLong(root, "apiErrorStatus") == 429, tools, skills));
            }
            catch (JsonException) { skipped++; }
        }
        measured(stream.Position - offset, lines);
        return new(events, skipped, complete, identity);
    }

    private static bool CanReplaceClaude(ClaudeSource previous, ClaudeSource next) =>
        next.Offset >= previous.Offset
        && (previous.Identity is null || previous.Identity == next.Identity)
        && next.Events.Count >= previous.Events.Count
        && previous.Events.Select(e => JsonSerializer.Serialize(e, IndexJson))
            .SequenceEqual(next.Events.Take(previous.Events.Count).Select(e => JsonSerializer.Serialize(e, IndexJson)));

    private async Task<(IReadOnlyDictionary<string, ClaudeSource> Sources, IndexStatus Index, int Conflicts, int Retained, LedgerRead<ClaudeSource>? History)> ReadSourcesAsync(
        List<string> diagnostics, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes();
        long bytesRead = 0;
        var linesParsed = 0;
        void Measured(long bytes, int lines) { bytesRead += bytes; linesParsed += lines; }
        async Task<string> Boundary(string file, long offset)
        {
            var result = await SourceBoundary.ReadAsync(file, offset, ct);
            bytesRead += offset;
            return result;
        }
        var root = applicationDataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        var indexPath = Path.Combine(root, "claude-session-index-v1.json");
        var cached = new Dictionary<string, ClaudeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        if (incrementalIndexEnabled && File.Exists(indexPath))
        {
            try
            {
                var index = JsonSerializer.Deserialize<ClaudeIndex>(await File.ReadAllTextAsync(indexPath, ct), IndexJson);
                if (index is { Version: 3 } && index.Zone == TimeZoneInfo.Local.Id && index.Entries is not null)
                    cached = new(index.Entries.Where(p => p.Value?.Source?.Events is not null
                        && p.Value.Source.Offset >= 0 && p.Value.Source.Offset <= p.Value.Length
                        && p.Value.Source.Events.All(e => e is not null && e.Tools is not null && e.Skills is not null)), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { diagnostics.Add("Claude 索引不可用，重新解析日志"); }
        }
        string[] files = [];
        try
        {
            var projects = Path.Combine(paths.ClaudeDirectory, "projects");
            if (Directory.Exists(projects)) files = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { diagnostics.Add($"无法枚举 Claude transcript：{e.Message}"); }
        var next = new Dictionary<string, ClaudeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var sources = new Dictionary<string, ClaudeSource>(StringComparer.Ordinal);
        var disputed = new HashSet<string>(StringComparer.Ordinal);
        var reused = 0; var appended = 0; var parsed = 0; var failures = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                var fileIdentity = SourceFileIdentity.Read(file);
                ClaudeSource source;
                cached.TryGetValue(file, out var entry);
                if (entry?.FileIdentity != fileIdentity) entry = null;
                if (incrementalIndexEnabled && entry is not null && entry.Length == info.Length && entry.Modified == info.LastWriteTimeUtc.Ticks)
                { source = entry.Source; reused++; }
                else if (incrementalIndexEnabled && entry is not null && info.Length > entry.Length
                    && entry.Boundary == await Boundary(file, entry.Source.Offset))
                { source = await ParseTranscriptAsync(file, entry.Source, Measured, ct); appended++; }
                else { source = await ParseTranscriptAsync(file, null, Measured, ct); parsed++; }
                var sourceId = source.Identity ?? UsageHistoryLedger.SourceId(file);
                if (!sources.TryGetValue(sourceId, out var prior) || CanReplaceClaude(prior, source)) sources[sourceId] = source;
                else if (!CanReplaceClaude(source, prior)) disputed.Add(sourceId);
                next[file] = new(info.Length, info.LastWriteTimeUtc.Ticks,
                    reused > 0 && ReferenceEquals(source, entry?.Source) ? entry!.Boundary : await Boundary(file, source.Offset), source, fileIdentity);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { failures++; diagnostics.Add($"跳过 Claude transcript：{Path.GetFileName(file)} · {e.Message}"); }
        }
        if (incrementalIndexEnabled && (parsed > 0 || appended > 0 || next.Count != cached.Count || failures > 0))
        {
            try
            {
                Directory.CreateDirectory(root);
                var temp = indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new ClaudeIndex(3, TimeZoneInfo.Local.Id, next), IndexJson), ct);
                    File.Move(temp, indexPath, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { diagnostics.Add($"Claude 索引写入失败：{e.Message}"); }
        }
        var indexStatus = new IndexStatus(incrementalIndexEnabled, reused, appended, parsed, files.Length, DateTimeOffset.Now);
        MetricsObserved?.Invoke(new(bytesRead, linesParsed, started.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes() - allocated));
        diagnostics.Add($"Claude 增量索引：复用 {reused}，续读 {appended}，重新解析 {parsed}");
        try
        {
            var history = await new UsageHistoryLedger(root).MergeAsync("claude", sources, CanReplaceClaude, ct, disputed);
            diagnostics.Add($"用量历史：留存 {history.RetainedSources} 个来源，冲突 {history.Conflicts} 个来源");
            return (history.Sources, indexStatus, history.Conflicts + failures, history.RetainedSources, history);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException)
        { diagnostics.Add($"用量历史不可用，使用当前日志：{e.Message}"); return (sources, indexStatus, failures, 0, null); }
    }
}
