using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record ClaudeDesktopQuotaResult(
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    DateTimeOffset? MeasuredAt,
    string Diagnostic)
{
    internal bool HasQuota => Primary is not null || Secondary is not null;
}

/// <summary>
/// Reads only Desktop's dedicated usage-history cache. This is an observed local
/// format, not a public stable API: reject unknown versions, ambiguous accounts,
/// stale samples and malformed data instead of guessing or reading auth storage.
/// </summary>
internal sealed class ClaudeDesktopQuotaReader(string? dataDirectory)
{
    internal const string Source = "claude-desktop-history";
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumSamples = 10_000;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private sealed record Sample(string Organization, DateTimeOffset MeasuredAt, RateLimitWindow? Primary, RateLimitWindow? Secondary);

    internal async Task<ClaudeDesktopQuotaResult> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return Unavailable(string.Empty);
        try
        {
            var files = ClaudeDesktopDiscovery.FindHistoryFiles(dataDirectory);
            if (files.Count == 0) return Unavailable("Claude Desktop 额度：未找到本地用量历史");
            string? organization = null;
            Sample? latest = null;
            foreach (var file in files)
            {
                using var document = await ReadDocumentAsync(file, cancellationToken);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                    || !version.TryGetInt32(out var number) || number != 2
                    || !root.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("历史格式不受支持（需要 version=2）");
                if (samples.GetArrayLength() > MaximumSamples) throw new InvalidDataException("历史样本数量超过安全上限");

                foreach (var item in samples.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sample = ReadSample(item);
                    if (organization is not null && organization != sample.Organization)
                        throw new InvalidDataException("历史包含多个组织，无法确认当前账户，已停止自动选择");
                    organization = sample.Organization;
                    if (latest is null || sample.MeasuredAt > latest.MeasuredAt) latest = sample;
                    else if (sample.MeasuredAt == latest.MeasuredAt && sample != latest)
                        throw new InvalidDataException("相同采样时间存在冲突，已停止自动选择");
                }
            }

            if (latest is null) return Unavailable("Claude Desktop 额度：尚无本地用量样本");
            if (latest.MeasuredAt > now + MaximumClockSkew)
                return Unavailable("Claude Desktop 额度：采样时间在未来（超过 5 分钟），已忽略");
            if (now - latest.MeasuredAt > MaximumAge)
                return Unavailable("Claude Desktop 额度：历史采样已过期（超过 30 分钟），请在 Desktop 中刷新用量");
            if (latest.Primary is null && latest.Secondary is null)
                return Unavailable("Claude Desktop 额度：最新样本没有可识别的 5 小时或 7 天窗口；不会从旧样本补齐");

            return new(latest.Primary, latest.Secondary, latest.MeasuredAt,
                $"Claude Desktop 额度：本地历史采样 {latest.MeasuredAt:O}，非实时；来源不提供重置时间或订阅套餐");
        }
        catch (InvalidDataException exception)
        {
            return Unavailable($"Claude Desktop 额度：{exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                           or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Never expose an organization identifier, arbitrary JSON or a source
            // exception containing user data through diagnostics.
            return Unavailable("Claude Desktop 额度：本地历史无法安全读取，继续使用其他可用来源");
        }
    }

    private static ClaudeDesktopQuotaResult Unavailable(string diagnostic) => new(null, null, null, diagnostic);

    private static async Task<JsonDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > MaximumFileBytes) throw new InvalidDataException("历史文件超过 2 MiB 安全上限");
        var bytes = new byte[(int)length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.Length != length) throw new InvalidDataException("历史文件正在更新，请稍后刷新");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
    }

    private static Sample ReadSample(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("t", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number
            || !timestamp.TryGetInt64(out var milliseconds)
            || !item.TryGetProperty("org", out var org) || org.ValueKind != JsonValueKind.String
            || org.GetString() is not { Length: > 0 and <= 128 } organization || string.IsNullOrWhiteSpace(organization)
            || !item.TryGetProperty("u", out var usage) || usage.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("历史样本缺少有效的采样时间、组织标识或用量字段");
        DateTimeOffset measuredAt;
        try { measuredAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException) { throw new InvalidDataException("历史样本的毫秒时间戳无效"); }
        return new(organization, measuredAt, ReadWindow(usage, "fh", 300, measuredAt), ReadWindow(usage, "sd", 10080, measuredAt));
    }

    private static RateLimitWindow? ReadWindow(JsonElement usage, string name, int minutes, DateTimeOffset measuredAt)
    {
        if (!usage.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var percent)
            || !double.IsFinite(percent) || percent is < 0 or > 100)
            throw new InvalidDataException("历史窗口百分比无效，已忽略该来源");
        // A sampling timestamp is not the start of a quota window. Never derive a
        // reset from it, or map model-specific / extra-spend buckets to these rings.
        return new(percent, minutes, ResetsAt: null, MeasuredAt: measuredAt, Source: Source);
    }
}
