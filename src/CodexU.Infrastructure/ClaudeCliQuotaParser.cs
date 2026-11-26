using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record ClaudeCliQuotaReading(
    ClaudeCliQuotaStatus Status,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    DateTimeOffset? ObservedAt,
    string Diagnostic)
{
    internal bool Success => Status is ClaudeCliQuotaStatus.Fresh or ClaudeCliQuotaStatus.NoWindows;
}

/// <summary>
/// Normalizes the CLI's get_usage control response. Only the general session and
/// all-models weekly windows are kept; model-scoped windows, spend, account and
/// plan fields are never retained. The control protocol has no public contract,
/// so anything unexpected yields Unavailable rather than a guessed value.
/// </summary>
internal static class ClaudeCliQuotaParser
{
    internal const string Source = "claude-cli-get-usage";
    internal const int MaximumResponseBytes = 256 * 1024;
    private const int MaximumWindows = 64;
    private static readonly Regex ZonedTimestamp = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static ClaudeCliQuotaReading Parse(string line, string requestId, DateTimeOffset observedAt)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(line) > MaximumResponseBytes) return Unavailable(observedAt, "响应过大");
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            return ParseResponse(document.RootElement, requestId, observedAt);
        }
        catch (JsonException) { return Unavailable(observedAt, "响应不是有效 JSON"); }
        catch (InvalidOperationException) { return Unavailable(observedAt, "响应格式无效"); }
    }

    /// <summary>True when the line is the control response for this request, whatever its outcome.</summary>
    internal static bool IsResponseFor(string line, string requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            return Text(root, "type") == "control_response"
                && root.TryGetProperty("response", out var response)
                && Text(response, "request_id") == requestId;
        }
        catch (JsonException) { return false; }
    }

    private static ClaudeCliQuotaReading ParseResponse(JsonElement root, string requestId, DateTimeOffset observedAt)
    {
        if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root)
            || Text(root, "type") != "control_response"
            || !root.TryGetProperty("response", out var envelope) || envelope.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(envelope) || Text(envelope, "request_id") != requestId)
            return Unavailable(observedAt, "控制响应与请求不匹配");
        if (Text(envelope, "subtype") != "success") return Unavailable(observedAt, "CLI 拒绝了额度查询");
        if (!envelope.TryGetProperty("response", out var usage) || usage.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(usage))
            return Unavailable(observedAt, "响应缺少用量结果");
        if (!usage.TryGetProperty("rate_limits_available", out var available)
            || available.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Unavailable(observedAt, "响应缺少额度可用性");
        if (!available.GetBoolean())
            return new(ClaudeCliQuotaStatus.NotAvailable, null, null, observedAt,
                "Claude CLI 后台额度：当前 CLI 登录没有可用的订阅额度（未登录、API 密钥或第三方服务）");
        if (!usage.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object
            || HasDuplicateProperties(limits))
            return Unavailable(observedAt, "服务端未返回额度数据");
        if (!limits.TryGetProperty("limits", out var windows) || windows.ValueKind != JsonValueKind.Array)
            return Unavailable(observedAt, "额度窗口格式无效");
        if (windows.GetArrayLength() > MaximumWindows) return Unavailable(observedAt, "额度窗口数量超限");

        RateLimitWindow? primary = null;
        RateLimitWindow? secondary = null;
        foreach (var item in windows.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || HasDuplicateProperties(item))
                return Unavailable(observedAt, "额度窗口格式无效");
            var kind = Text(item, "kind");
            // Only unscoped windows describe the whole account; a model- or
            // surface-scoped weekly window must never fill the general ring.
            var general = !item.TryGetProperty("scope", out var scope) || scope.ValueKind == JsonValueKind.Null;
            if (!general || kind is not ("session" or "weekly_all")) continue;
            if (!item.TryGetProperty("percent", out var percentValue) || percentValue.ValueKind != JsonValueKind.Number
                || !percentValue.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent < 0)
                return Unavailable(observedAt, "额度百分比无效");
            DateTimeOffset? resetsAt = null;
            if (item.TryGetProperty("resets_at", out var reset) && reset.ValueKind != JsonValueKind.Null)
            {
                if (reset.ValueKind != JsonValueKind.String || reset.GetString() is not { Length: <= 40 } timestamp
                    || !ZonedTimestamp.IsMatch(timestamp)
                    || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return Unavailable(observedAt, "重置时间无效");
                resetsAt = parsed.ToUniversalTime();
            }
            var isSession = kind == "session";
            if (isSession ? primary is not null : secondary is not null) return Unavailable(observedAt, "额度窗口重复");
            var window = new RateLimitWindow(Math.Min(percent, 100d), isSession ? 300 : 10080, resetsAt, observedAt, Source);
            if (isSession) primary = window;
            else secondary = window;
        }

        return primary is null && secondary is null
            ? new(ClaudeCliQuotaStatus.NoWindows, null, null, observedAt, "Claude CLI 后台额度：查询成功，没有一般额度窗口")
            : new(ClaudeCliQuotaStatus.Fresh, primary, secondary, observedAt, $"Claude CLI 后台额度：读取于 {observedAt:O}");
    }

    internal static ClaudeCliQuotaReading Unavailable(DateTimeOffset? observedAt, string reason) =>
        new(ClaudeCliQuotaStatus.Unavailable, null, null, observedAt, $"Claude CLI 后台额度：{reason}，稍后自动重试");

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name));
    }
}
