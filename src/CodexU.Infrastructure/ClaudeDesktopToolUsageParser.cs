using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record ClaudeDesktopToolUsageParseResult(
    bool Success,
    DateTimeOffset ObservedAt,
    RateLimitWindow? Primary = null,
    RateLimitWindow? Secondary = null,
    string? Reason = null);

internal static class ClaudeDesktopToolUsageParser
{
    internal const string ToolName = "mcp__ccd_session_mgmt__get_usage";
    internal const string Source = "claude-desktop-get-usage";
    private const int MaximumContentBytes = 256 * 1024;
    private const int MaximumWindows = 64;
    private static readonly Regex ZonedTimestamp = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Normalizes one paired native call/result; it never scans files or accepts
    /// assistant prose. ObservedAt is the transcript event time, not a claimed
    /// server measurement time (Desktop can cache the source for up to a minute).
    /// No account identity, plan label, spending amount or tool-call ID is retained.
    /// </summary>
    internal static ClaudeDesktopToolUsageParseResult Parse(
        JsonElement toolUse, JsonElement toolResult, DateTimeOffset observedAt)
    {
        try { return ParseNativePair(toolUse, toolResult, observedAt); }
        catch (InvalidOperationException) { return Failed(observedAt, "invalid_envelope"); }
    }

    private static ClaudeDesktopToolUsageParseResult ParseNativePair(
        JsonElement toolUse, JsonElement toolResult, DateTimeOffset observedAt)
    {
        if (Text(toolUse, "type") != "tool_use" || Text(toolResult, "type") != "tool_result"
            || !IsIdentifier(Text(toolUse, "id")) || !IsIdentifier(Text(toolResult, "tool_use_id"))
            || HasDuplicateProperties(toolUse) || HasDuplicateProperties(toolResult))
            return Failed(observedAt, "invalid_envelope");
        if (Text(toolUse, "name") != ToolName || Text(toolUse, "id") != Text(toolResult, "tool_use_id"))
            return Failed(observedAt, "tool_mismatch");
        foreach (var name in new[] { "is_error", "isError" })
        {
            if (!toolResult.TryGetProperty(name, out var error)) continue;
            if (error.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Failed(observedAt, "invalid_envelope");
            if (error.GetBoolean()) return Failed(observedAt, "tool_error");
        }
        if (!toolResult.TryGetProperty("content", out var content)) return Failed(observedAt, "invalid_content");
        string? text;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
        {
            if (content.GetArrayLength() > 1) return Failed(observedAt, "ambiguous_content");
            if (content.GetArrayLength() != 1 || Text(content[0], "type") != "text") return Failed(observedAt, "invalid_content");
            text = Text(content[0], "text");
        }
        else return Failed(observedAt, "invalid_content");
        if (string.IsNullOrWhiteSpace(text)) return Failed(observedAt, "invalid_content");
        if (text.Length > MaximumContentBytes || Encoding.UTF8.GetByteCount(text) > MaximumContentBytes)
            return Failed(observedAt, "payload_too_large");
        try { return ParsePayload(text, observedAt); }
        catch (JsonException) { return Failed(observedAt, "invalid_json"); }
    }

    private static ClaudeDesktopToolUsageParseResult ParsePayload(string text, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("plan", out var plan)
            || plan.ValueKind != JsonValueKind.Object) return Failed(observedAt, "invalid_plan");
        if (HasDuplicateProperties(root) || HasDuplicateProperties(plan)) return Failed(observedAt, "ambiguous_payload");
        var status = Text(plan, "status");
        if (status != "ok") return Failed(observedAt, status switch
        {
            "unavailable" => "plan_unavailable",
            "not_applicable" => "plan_not_applicable",
            _ => "unsupported_plan_status"
        });
        if (!plan.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
            return Failed(observedAt, "invalid_windows");
        if (windows.GetArrayLength() > MaximumWindows) return Failed(observedAt, "too_many_windows");
        RateLimitWindow? primary = null;
        RateLimitWindow? secondary = null;
        foreach (var item in windows.EnumerateArray())
        {
            var label = Text(item, "label");
            if (label is not { Length: > 0 and <= 160 } || string.IsNullOrWhiteSpace(label))
                return Failed(observedAt, "invalid_window");
            if (HasDuplicateProperties(item)) return Failed(observedAt, "ambiguous_payload");
            if (!item.TryGetProperty("percentUsed", out var used) || used.ValueKind != JsonValueKind.Number
                || !used.TryGetDouble(out var percent) || !double.IsFinite(percent) || percent is < 0 or > 100)
                return Failed(observedAt, "invalid_percent");
            DateTimeOffset? resetsAt = null;
            if (item.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
            {
                if (reset.ValueKind != JsonValueKind.String || reset.GetString() is not { Length: <= 40 } timestamp
                    || !ZonedTimestamp.IsMatch(timestamp)
                    || !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    return Failed(observedAt, "invalid_reset");
                resetsAt = parsed.ToUniversalTime();
            }
            // These labels are the fixed native formatter's general windows.
            // Model-scoped labels must never fill the account-wide weekly ring.
            if (label is not ("5-hour limit" or "Weekly · all models")) continue;
            if (label == "5-hour limit" ? primary is not null : secondary is not null)
                return Failed(observedAt, "ambiguous_window");
            var window = new RateLimitWindow(percent,
                label == "5-hour limit" ? 300 : 10080, resetsAt, observedAt, Source);
            if (label == "5-hour limit") primary = window;
            else secondary = window;
        }
        return new(true, observedAt, primary, secondary);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value);

    private static bool HasDuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static ClaudeDesktopToolUsageParseResult Failed(DateTimeOffset observedAt, string reason) =>
        new(false, observedAt, Reason: reason);
}
