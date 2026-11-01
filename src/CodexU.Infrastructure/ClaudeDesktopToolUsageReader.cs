using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record ClaudeDesktopToolUsageReadResult(
    bool Success,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    DateTimeOffset? ObservedAt,
    string Diagnostic,
    bool FoundResponse);

internal sealed class ClaudeDesktopToolUsageReader(string? filePath)
{
    private const int MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumPendingCalls = 64;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private static readonly Regex ZonedTimestamp = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    internal async Task<ClaudeDesktopToolUsageReadResult> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latest = Unavailable("未找到匹配的原生工具结果");
        try
        {
            var path = ValidatePath(filePath, out var sessionId);
            RejectLinks(path);
            var modifiedAt = File.GetLastWriteTimeUtc(path);
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = source.Length;
            if (length > MaximumFileBytes) return Unavailable("指定会话文件超过 32 MiB 安全上限");

            // Capture at most the already-bounded file size. An append must not
            // turn a single read into an unbounded scan of a live transcript.
            var bytes = new byte[(int)length];
            await source.ReadExactlyAsync(bytes, cancellationToken);
            if (source.Length != length || File.GetLastWriteTimeUtc(path) != modifiedAt)
                return Unavailable("指定会话文件正在更新，请稍后刷新");

            using var snapshot = new MemoryStream(bytes, writable: false);
            var reader = new BoundedLineReader(snapshot);
            var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            while (true)
            {
                var line = await reader.ReadAsync(cancellationToken);
                if (line.IsEndOfStream) break;
                if (line.IsTooLong) return Unavailable("会话包含超过 4 MiB 的行，已停止读取", latest.FoundResponse);
                if (!line.IsTerminated) return Unavailable("会话尾行尚未写完，请稍后刷新", latest.FoundResponse);
                if (string.IsNullOrWhiteSpace(line.Line)) continue;
                using var document = JsonDocument.Parse(line.Line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Unavailable("会话记录格式无效", latest.FoundResponse);
                if (HasDuplicateFields(root, "type", "sessionId", "timestamp", "message"))
                    return Unavailable("会话身份或采样字段存在歧义", latest.FoundResponse);
                var type = Text(root, "type");
                if (type is not ("assistant" or "user")) continue;
                if (!Guid.TryParseExact(Text(root, "sessionId"), "D", out var recordSession) || recordSession != sessionId)
                    return Unavailable("会话身份与指定文件不一致", latest.FoundResponse);
                if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                if (HasDuplicateFields(message, "role", "content"))
                    return Unavailable("会话消息字段存在歧义", latest.FoundResponse);
                if (Text(message, "role") is { } role && role != type)
                    return Unavailable("会话消息角色无效", latest.FoundResponse);
                if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var block in content.EnumerateArray())
                {
                    if (type == "assistant" && Text(block, "type") == "tool_use"
                        && Text(block, "name") == ClaudeDesktopToolUsageParser.ToolName)
                    {
                        var id = Text(block, "id");
                        if (!IsIdentifier(id) || pending.Count >= MaximumPendingCalls || !pending.TryAdd(id!, block.Clone()))
                            return Unavailable("原生工具调用标识无效、重复或待配对数量超限", latest.FoundResponse);
                    }
                    else if (type == "user" && Text(block, "type") == "tool_result"
                        && Text(block, "tool_use_id") is { } id && pending.Remove(id, out var use))
                    {
                        // File order decides the most recent paired response. A
                        // failed, empty or stale result must never revive an old one.
                        latest = Unavailable("最新原生结果缺少带时区的有效采样时间", foundResponse: true);
                        if (!TryTimestamp(Text(root, "timestamp"), out var observedAt)) continue;
                        var parsed = ClaudeDesktopToolUsageParser.Parse(use, block, observedAt);
                        latest = parsed.Success
                            ? new(true, parsed.Primary, parsed.Secondary, observedAt,
                                "Claude Desktop 原生额度：已读取指定会话的工具结果", true)
                            : Unavailable("最新原生工具结果不可用", foundResponse: true, observedAt);
                    }
                }
            }

            if (!latest.Success || latest.ObservedAt is not { } measuredAt) return latest;
            if (measuredAt - now > MaximumClockSkew)
                return Unavailable("最新原生结果采样时间在未来（超过 5 分钟）", true, measuredAt);
            if (now - measuredAt > MaximumAge)
                return Unavailable("最新原生结果已过期（超过 15 分钟），请在 Desktop 重新获取用量", true, measuredAt);

            var primary = DropExpired(latest.Primary, now);
            var secondary = DropExpired(latest.Secondary, now);
            return latest with
            {
                Primary = primary,
                Secondary = secondary,
                Diagnostic = primary is null && secondary is null
                    ? "Claude Desktop 原生额度：最新结果没有仍有效的一般额度窗口"
                    : $"Claude Desktop 原生额度：原生结果读取于 {measuredAt:O}"
            };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or System.Security.SecurityException or InvalidOperationException)
        {
            // Do not expose paths, session IDs, tool input or arbitrary exceptions.
            return Unavailable("指定会话无法安全读取，未使用其中的旧结果", latest.FoundResponse);
        }
    }

    private static ClaudeDesktopToolUsageReadResult Unavailable(
        string reason, bool foundResponse = false, DateTimeOffset? observedAt = null) =>
        new(false, null, null, observedAt, "Claude Desktop 原生额度：" + reason, foundResponse);

    private static RateLimitWindow? DropExpired(RateLimitWindow? window, DateTimeOffset now) =>
        window?.ResetsAt is { } reset && reset <= now ? null : window;

    private static bool TryTimestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= 40 } && ZonedTimestamp.IsMatch(text)
            && DateTimeOffset.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    private static string? Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value);

    private static bool HasDuplicateFields(JsonElement value, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().Any(property => names.Contains(property.Name, StringComparer.Ordinal)
            && !seen.Add(property.Name));
    }

    private static string ValidatePath(string? value, out Guid sessionId)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported path.");
        var path = Path.GetFullPath(value);
        if (OperatingSystem.IsWindows() && (path.Length < 3 || !char.IsAsciiLetter(path[0])
            || path[1] != ':' || path.AsSpan(2).Contains(':')))
            throw new InvalidDataException("Unsupported path.");
        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out sessionId))
            throw new InvalidDataException("Unsupported file name.");
        return path;
    }

    private static void RejectLinks(string path)
    {
        // Inspect parents from the local root down, before touching a child that
        // could otherwise resolve through a junction to a network or secret path.
        var parents = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            parents.Push(parent);
        foreach (var candidate in parents.Append(path))
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked source is not supported.");
    }
}
