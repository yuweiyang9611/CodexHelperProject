using System.Text;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal readonly record struct AntigravityModelCall(
    string Model,
    string? ResponseId,
    TokenBreakdown Tokens);

/// <summary>
/// Minimal, zero-dependency wire-format parser for Antigravity's schemaless
/// <c>gen_metadata.data</c> protobuf BLOBs.
///
/// Confirmed field paths:
/// - 1.19 = model id (e.g. "gemini-4-neon")
/// - 1.21 = display name fallback (e.g. "Gemini 4 Neon (High)")
/// - 1.4  = usage submessage:
///     - 1.4.2  = input tokens (includes cache read)
///     - 1.4.5  = cache read tokens (subset of input)
///     - 1.4.3  = output tokens (includes thinking)
///     - 1.4.9  = thinking tokens
///     - 1.4.10 = visible output tokens (1.4.3 = 1.4.9 + 1.4.10)
///     - 1.4.7  = response id (deduplication key)
/// - 1.17.2 is a duplicate of 1.4 and is intentionally ignored.
/// - Interrupted calls may omit 1.4 and are skipped without error.
/// </summary>
internal static class AntigravityGenMetadataParser
{
    private const long MaxReasonableTokensPerCall = 1_000_000_000_000L;
    private const int MaxStringBytes = 512;

    public static bool TryParse(ReadOnlySpan<byte> data, out AntigravityModelCall call, out bool malformed)
    {
        call = default;
        malformed = false;
        if (data.IsEmpty) return false;

        var offset = 0;
        var foundWrapper = false;
        string? modelId = null;
        string? displayName = null;
        var hasUsage = false;
        UsageFields usage = default;

        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out var fieldNumber, out var wireType))
            {
                malformed = true;
                return false;
            }

            if (fieldNumber == 1 && wireType == 2)
            {
                if (!TryReadLengthDelimited(data, ref offset, out var inner))
                {
                    malformed = true;
                    return false;
                }

                foundWrapper = true;
                if (!TryParseOuterMessage(inner, ref modelId, ref displayName, ref hasUsage, ref usage))
                {
                    malformed = true;
                    return false;
                }
            }
            else if (!TrySkipField(data, ref offset, wireType))
            {
                malformed = true;
                return false;
            }
        }

        if (!foundWrapper || !hasUsage) return false;

        var total = usage.InputTokens + usage.OutputTokens;
        if (total <= 0) return false;

        var model = !string.IsNullOrWhiteSpace(modelId)
            ? modelId.Trim()
            : !string.IsNullOrWhiteSpace(displayName)
                ? displayName.Trim()
                : "antigravity-unknown";
        var responseId = string.IsNullOrWhiteSpace(usage.ResponseId) ? null : usage.ResponseId.Trim();
        var breakdown = new TokenBreakdown(
            usage.InputTokens,
            usage.CacheReadTokens,
            usage.OutputTokens,
            usage.ThinkingTokens,
            total);
        call = new AntigravityModelCall(model, responseId, breakdown);
        return true;
    }

    private static bool TryParseOuterMessage(
        ReadOnlySpan<byte> data,
        ref string? modelId,
        ref string? displayName,
        ref bool hasUsage,
        ref UsageFields usage)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out var fieldNumber, out var wireType))
                return false;

            switch (fieldNumber, wireType)
            {
                case (19, 2):
                    if (!TryReadUtf8(data, ref offset, out var id)) return false;
                    if (!string.IsNullOrWhiteSpace(id)) modelId = id;
                    break;
                case (21, 2):
                    if (!TryReadUtf8(data, ref offset, out var name)) return false;
                    if (!string.IsNullOrWhiteSpace(name)) displayName = name;
                    break;
                case (4, 2):
                    if (!TryReadLengthDelimited(data, ref offset, out var usageBytes)) return false;
                    if (!TryParseUsageMessage(usageBytes, ref usage)) return false;
                    hasUsage = true;
                    break;
                default:
                    // Field 17 (1.17.2 duplicate of 1.4), field 20 (key/value metadata),
                    // and any unknown fields are skipped without counting.
                    if (!TrySkipField(data, ref offset, wireType)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool TryParseUsageMessage(ReadOnlySpan<byte> data, ref UsageFields usage)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadTag(data, ref offset, out var fieldNumber, out var wireType))
                return false;

            switch (fieldNumber, wireType)
            {
                case (2, 0):
                    if (!TryReadTokenCount(data, ref offset, out var input)) return false;
                    usage.InputTokens = input;
                    break;
                case (3, 0):
                    if (!TryReadTokenCount(data, ref offset, out var output)) return false;
                    usage.OutputTokens = output;
                    break;
                case (5, 0):
                    if (!TryReadTokenCount(data, ref offset, out var cacheRead)) return false;
                    usage.CacheReadTokens = cacheRead;
                    break;
                case (7, 2):
                    if (!TryReadUtf8(data, ref offset, out var responseId)) return false;
                    if (!string.IsNullOrWhiteSpace(responseId)) usage.ResponseId = responseId;
                    break;
                case (9, 0):
                    if (!TryReadTokenCount(data, ref offset, out var thinking)) return false;
                    usage.ThinkingTokens = thinking;
                    break;
                case (10, 0):
                    // Visible output tokens (already included in 1.4.3). Validate wire encoding only.
                    if (!TryReadTokenCount(data, ref offset, out _)) return false;
                    break;
                default:
                    if (!TrySkipField(data, ref offset, wireType)) return false;
                    break;
            }
        }

        return true;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> data, ref int offset, out int fieldNumber, out int wireType)
    {
        fieldNumber = 0;
        wireType = 0;
        if (!TryReadVarint(data, ref offset, out var tag) || tag == 0)
            return false;

        wireType = (int)(tag & 0x7UL);
        var rawField = tag >> 3;
        if (rawField == 0 || rawField > int.MaxValue)
            return false;

        fieldNumber = (int)rawField;
        return true;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            if (offset >= data.Length) return false;
            var b = data[offset++];
            // The 10th byte of a 64-bit varint may only carry bit 0.
            if (i == 9 && (b & 0xFE) != 0) return false;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }

        return false;
    }

    private static bool TryReadTokenCount(ReadOnlySpan<byte> data, ref int offset, out long count)
    {
        count = 0;
        if (!TryReadVarint(data, ref offset, out var raw)) return false;
        if (raw > (ulong)MaxReasonableTokensPerCall) return false;
        count = (long)raw;
        return true;
    }

    private static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(data, ref offset, out var rawLength)) return false;
        if (rawLength > (ulong)(data.Length - offset)) return false;
        var length = (int)rawLength;
        slice = data.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadUtf8(ReadOnlySpan<byte> data, ref int offset, out string text)
    {
        text = string.Empty;
        if (!TryReadLengthDelimited(data, ref offset, out var slice)) return false;
        if (slice.Length == 0) return true;
        if (slice.Length > MaxStringBytes)
        {
            // Keep parsing safely without allocating huge strings for unexpected blobs.
            return true;
        }

        text = Encoding.UTF8.GetString(slice);
        return true;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, ref int offset, int wireType) => wireType switch
    {
        0 => TryReadVarint(data, ref offset, out _),
        1 => TryAdvance(data.Length, ref offset, 8),
        2 => TryReadLengthDelimited(data, ref offset, out _),
        5 => TryAdvance(data.Length, ref offset, 4),
        _ => false
    };

    private static bool TryAdvance(int totalLength, ref int offset, int count)
    {
        if (count < 0 || count > totalLength - offset) return false;
        offset += count;
        return true;
    }

    private struct UsageFields
    {
        public long InputTokens;
        public long CacheReadTokens;
        public long OutputTokens;
        public long ThinkingTokens;
        public string? ResponseId;
    }
}
