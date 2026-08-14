using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed partial class CodexSessionReader
{
    public static async Task<ParsedSessionFile> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        await ParseFileAsync(path, null, 0, cancellationToken);

    private static async Task<ParsedSessionFile> ParseFileAsync(
        string path,
        ParsedSessionFile? seed,
        long offset,
        CancellationToken cancellationToken)
    {
        var usageBuckets = seed?.UsageBuckets.ToDictionary(
            bucket => (bucket.Date, bucket.Model),
            bucket => bucket,
            UsageBucketKeyComparer.Instance)
            ?? new Dictionary<(DateOnly Date, string Model), SessionUsageBucket>(UsageBucketKeyComparer.Instance);
        var tools = seed?.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skills = seed?.Skills.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tokenEvents = seed?.TokenEvents.ToList() ?? [];
        var tokenCounterState = seed?.TokenCounterState ?? TokenCounterState.Empty;
        var sessionId = seed?.SessionId;
        var forkedFromId = seed?.ForkedFromId;
        var currentModel = seed?.CurrentModel ?? "unknown";
        var tokenEventCount = seed?.TokenEventCount ?? 0;
        var taskStarted = seed?.TaskLifecycle.Started ?? 0;
        var taskCompleted = seed?.TaskLifecycle.Completed ?? 0;
        var taskAborted = seed?.TaskLifecycle.Aborted ?? 0;
        long taskDuration = seed?.TaskLifecycle.DurationMilliseconds ?? 0;
        long longestTaskDuration = seed?.TaskLifecycle.LongestDurationMilliseconds ?? 0;
        var latestRateLimit = seed?.LatestRateLimit;
        var skippedLineCount = seed?.SkippedLineCount ?? 0;
        var forkReplayParentId = seed?.ForkReplayParentId;
        var forkReplayPhase = seed?.ForkReplayPhase ?? ForkReplayPhase.Uninspected;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);
        var completeOffset = await FindLastCompleteLineOffsetAsync(stream, offset, cancellationToken);
        stream.Seek(offset, SeekOrigin.Begin);

        using var boundedStream = new BoundedReadStream(stream, Math.Max(0, completeOffset - offset));
        var reader = new BoundedLineReader(boundedStream);

        while (true)
        {
            var read = await reader.ReadAsync(cancellationToken);
            if (read.IsEndOfStream)
            {
                break;
            }

            if (read.IsTooLong)
            {
                skippedLineCount++;
                continue;
            }

            var line = read.Line!;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestamp = ReadTimestamp(root) ?? File.GetLastWriteTimeUtc(path);
                var payload = root.TryGetProperty("payload", out var payloadValue) ? payloadValue : root;
                var rootType = GetString(root, "type");
                var type = GetString(payload, "type") ?? rootType;

                if (forkReplayPhase == ForkReplayPhase.Uninspected
                    && string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase))
                {
                    sessionId = GetString(payload, "id");
                    forkedFromId = GetString(payload, "forked_from_id");
                    if (TryReadThreadSpawnParentId(payload, out var discoveredParentId))
                    {
                        forkReplayParentId = discoveredParentId;
                        forkReplayPhase = ForkReplayPhase.AwaitingParentMetadata;
                    }
                    else
                    {
                        // Only the file header identifies the session itself. A
                        // session_meta copied later in the transcript must never
                        // reclassify an ordinary parent session as a spawned child.
                        forkReplayPhase = ForkReplayPhase.NotThreadSpawn;
                    }
                }
                else if (forkReplayPhase == ForkReplayPhase.AwaitingParentMetadata
                         && string.Equals(type, "session_meta", StringComparison.OrdinalIgnoreCase)
                         && string.Equals(
                             GetString(payload, "id"),
                             forkReplayParentId,
                             StringComparison.OrdinalIgnoreCase))
                {
                    forkReplayPhase = ForkReplayPhase.Replaying;
                }
                else if (forkReplayPhase == ForkReplayPhase.Replaying
                         && string.Equals(
                             rootType,
                             "inter_agent_communication_metadata",
                             StringComparison.OrdinalIgnoreCase))
                {
                    forkReplayPhase = ForkReplayPhase.Completed;
                }

                if (TryReadModel(payload, out var discoveredModel))
                {
                    currentModel = UsageCredits.NormalizeModel(discoveredModel);
                }

                var isForkReplay = forkReplayPhase == ForkReplayPhase.Replaying;

                if (string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase))
                {
                    var hasCumulative = TryReadCumulativeUsage(payload, out var cumulativeSample);
                    var hasLast = TryReadLastUsage(payload, out var last);
                    var cumulative = hasCumulative ? cumulativeSample : null;
                    if (hasCumulative || hasLast)
                    {
                        var observation = TokenCounterNormalizer.Observe(tokenCounterState, cumulative, last);
                        tokenCounterState = observation.State;
                        var delta = observation.Delta;
                        if (delta.VisibleTotalTokens > 0)
                        {
                            var localDate = DateOnly.FromDateTime(timestamp.ToLocalTime().Date);
                            tokenEvents.Add(new SessionTokenEvent(
                                localDate,
                                currentModel,
                                delta,
                                Fingerprint(cumulative, last),
                                isForkReplay));
                            if (!isForkReplay)
                            {
                                var key = (localDate, currentModel);
                                usageBuckets[key] = usageBuckets.TryGetValue(key, out var currentBucket)
                                    ? currentBucket with
                                    {
                                        Tokens = currentBucket.Tokens.Add(delta),
                                        EventCount = currentBucket.EventCount + 1
                                    }
                                    : new SessionUsageBucket(localDate, currentModel, delta, 1);
                            }
                        }

                        if (!isForkReplay)
                        {
                            tokenEventCount++;
                        }
                    }
                }

                if (isForkReplay)
                {
                    // A thread_spawn rollout embeds the parent's transcript before
                    // this child starts. Preserve only model and cumulative-token
                    // state so the first child delta has the correct baseline.
                    continue;
                }

                if (TryReadRateLimitSample(payload, timestamp, out var rateLimitSample))
                {
                    latestRateLimit = MergeRateLimitSamples(latestRateLimit, rateLimitSample);
                }

                if (TryReadToolName(payload, out var toolName))
                {
                    tools[toolName] = tools.GetValueOrDefault(toolName) + 1;
                    if (string.Equals(toolName, "Skill", StringComparison.OrdinalIgnoreCase)
                        && TryReadSkillName(payload, out var skillName))
                    {
                        skills[skillName] = skills.GetValueOrDefault(skillName) + 1;
                    }
                }

                if (string.Equals(type, "task_started", StringComparison.OrdinalIgnoreCase))
                {
                    taskStarted++;
                }
                else if (string.Equals(type, "task_complete", StringComparison.OrdinalIgnoreCase))
                {
                    taskCompleted++;
                    var duration = GetLong(payload, "duration_ms");
                    taskDuration += duration;
                    longestTaskDuration = Math.Max(longestTaskDuration, duration);
                }
                else if (string.Equals(type, "turn_aborted", StringComparison.OrdinalIgnoreCase))
                {
                    taskAborted++;
                    var duration = GetLong(payload, "duration_ms");
                    taskDuration += duration;
                    longestTaskDuration = Math.Max(longestTaskDuration, duration);
                }
            }
            catch (JsonException)
            {
                skippedLineCount++;
                // Session 文件最后一行可能仍在写入，忽略不完整 JSON。
            }
        }

        return new ParsedSessionFile(
            usageBuckets.Values
                .OrderBy(bucket => bucket.Date)
                .ThenBy(bucket => bucket.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            tools,
            skills,
            new TaskLifecycleStats(taskStarted, taskCompleted, taskAborted, taskDuration, longestTaskDuration),
            tokenEventCount,
            completeOffset,
            tokenCounterState,
            currentModel,
            latestRateLimit,
            skippedLineCount,
            forkReplayParentId,
            forkReplayPhase,
            sessionId,
            forkedFromId,
            tokenEvents);
    }

    private static SessionTokenEventFingerprint Fingerprint(
        TokenCounterSample? cumulative,
        TokenCounterSample? last)
    {
        // Two independent stable 64-bit streams make accidental parent/child
        // collisions vanishingly unlikely without retaining raw JSON in the index.
        // The date and model are deliberately excluded: a fork replay preserves
        // the usage counters even when its copied events are written at a new time.
        const ulong firstOffset = 14695981039346656037UL;
        const ulong firstPrime = 1099511628211UL;
        const ulong secondOffset = 7809847782465536322UL;
        const ulong secondPrime = 14029467366897019727UL;
        var first = firstOffset;
        var second = secondOffset;

        MixFingerprint(ref first, cumulative is null ? 0UL : 1UL, firstPrime);
        MixFingerprint(ref second, cumulative is null ? 0UL : 1UL, secondPrime);
        if (cumulative is not null)
        {
            MixFingerprint(ref first, (ulong)cumulative.Fields, firstPrime);
            MixFingerprint(ref second, (ulong)cumulative.Fields, secondPrime);
            MixFingerprint(ref first, cumulative.Tokens, firstPrime);
            MixFingerprint(ref second, cumulative.Tokens, secondPrime);
        }
        MixFingerprint(ref first, last is null ? 0UL : 1UL, firstPrime);
        MixFingerprint(ref second, last is null ? 0UL : 1UL, secondPrime);
        if (last is not null)
        {
            MixFingerprint(ref first, (ulong)last.Fields, firstPrime);
            MixFingerprint(ref second, (ulong)last.Fields, secondPrime);
            MixFingerprint(ref first, last.Tokens, firstPrime);
            MixFingerprint(ref second, last.Tokens, secondPrime);
        }

        return new SessionTokenEventFingerprint(first, second);
    }

    private static void MixFingerprint(ref ulong hash, TokenBreakdown tokens, ulong prime)
    {
        MixFingerprint(ref hash, unchecked((ulong)tokens.InputTokens), prime);
        MixFingerprint(ref hash, unchecked((ulong)tokens.CachedInputTokens), prime);
        MixFingerprint(ref hash, unchecked((ulong)tokens.OutputTokens), prime);
        MixFingerprint(ref hash, unchecked((ulong)tokens.ReasoningOutputTokens), prime);
        MixFingerprint(ref hash, unchecked((ulong)tokens.TotalTokens), prime);
    }

    private static void MixFingerprint(ref ulong hash, ulong value, ulong prime)
    {
        unchecked
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                hash ^= (byte)(value >> shift);
                hash *= prime;
            }
        }
    }

    private static bool TryReadThreadSpawnParentId(JsonElement payload, out string parentId)
    {
        parentId = string.Empty;
        if (!string.Equals(GetString(payload, "thread_source"), "subagent", StringComparison.OrdinalIgnoreCase)
            || !payload.TryGetProperty("source", out var source)
            || source.ValueKind != JsonValueKind.Object
            || !source.TryGetProperty("subagent", out var subagent)
            || subagent.ValueKind != JsonValueKind.Object
            || !subagent.TryGetProperty("thread_spawn", out var threadSpawn))
        {
            return false;
        }

        parentId = GetString(payload, "parent_thread_id")
            ?? GetString(payload, "forked_from_id")
            ?? GetString(threadSpawn, "parent_thread_id")
            ?? GetString(threadSpawn, "forked_from_id")
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(parentId);
    }

    private static async Task<long> FindLastCompleteLineOffsetAsync(
        FileStream stream,
        long startOffset,
        CancellationToken cancellationToken)
    {
        var length = stream.Length;
        if (length <= startOffset)
        {
            return startOffset;
        }

        stream.Seek(length - 1, SeekOrigin.Begin);
        var lastByte = new byte[1];
        await stream.ReadExactlyAsync(lastByte, cancellationToken);
        if (lastByte[0] == (byte)'\n')
        {
            return length;
        }

        var buffer = new byte[8 * 1024];
        var position = length;
        while (position > startOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, position - startOffset);
            position -= count;
            stream.Seek(position, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    var tailStart = position + index + 1;
                    return await IsCompleteJsonValueAsync(stream, tailStart, length, cancellationToken)
                        ? length
                        : tailStart;
                }
            }
        }

        return await IsCompleteJsonValueAsync(stream, startOffset, length, cancellationToken)
            ? length
            : startOffset;
    }

    private static async Task<bool> IsCompleteJsonValueAsync(
        FileStream stream,
        long startOffset,
        long endOffset,
        CancellationToken cancellationToken)
    {
        const int maximumTailBytes = BoundedLineReader.DefaultMaximumLineBytes;
        var byteCount = endOffset - startOffset;
        if (byteCount <= 0 || byteCount > maximumTailBytes)
        {
            return false;
        }

        var bytes = new byte[(int)byteCount];
        stream.Seek(startOffset, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        try
        {
            using var _ = JsonDocument.Parse(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IEnumerable<string> EnumerateSessionFiles()
    {
        if (Directory.Exists(paths.SessionsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(paths.SessionsDirectory, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }

        if (Directory.Exists(paths.ArchivedSessionsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(paths.ArchivedSessionsDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }
    }

    private static bool TryReadModel(JsonElement payload, out string model)
    {
        model = GetString(payload, "model") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(model))
        {
            return true;
        }

        foreach (var containerName in new[] { "thread_settings", "session_meta", "info" })
        {
            if (payload.TryGetProperty(containerName, out var container)
                && container.ValueKind == JsonValueKind.Object)
            {
                model = GetString(container, "model") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(model))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadCumulativeUsage(JsonElement payload, out TokenCounterSample usage)
    {
        usage = new TokenCounterSample(TokenBreakdown.Zero, TokenUsageFields.None);
        JsonElement candidate;
        if (payload.TryGetProperty("info", out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("total_token_usage", out candidate))
        {
            usage = ReadCounterSample(candidate);
            return usage.Fields != TokenUsageFields.None;
        }

        if (payload.TryGetProperty("total_token_usage", out candidate)
            || payload.TryGetProperty("token_usage", out candidate))
        {
            usage = ReadCounterSample(candidate);
            return usage.Fields != TokenUsageFields.None;
        }

        return false;
    }

    private static bool TryReadLastUsage(JsonElement payload, out TokenCounterSample? usage)
    {
        usage = null;
        JsonElement candidate;
        if (payload.TryGetProperty("info", out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("last_token_usage", out candidate))
        {
            var sample = ReadCounterSample(candidate);
            usage = sample.Fields == TokenUsageFields.None ? null : sample;
            return usage is not null;
        }

        if (payload.TryGetProperty("last_token_usage", out candidate))
        {
            var sample = ReadCounterSample(candidate);
            usage = sample.Fields == TokenUsageFields.None ? null : sample;
            return usage is not null;
        }

        return false;
    }

    private static TokenCounterSample ReadCounterSample(JsonElement element)
    {
        var fields = TokenUsageFields.None;
        var input = ReadTokenField(element, "input_tokens", TokenUsageFields.Input, ref fields);
        var cachedInput = ReadTokenField(element, "cached_input_tokens", TokenUsageFields.CachedInput, ref fields);
        var output = ReadTokenField(element, "output_tokens", TokenUsageFields.Output, ref fields);
        var reasoning = ReadTokenField(element, "reasoning_output_tokens", TokenUsageFields.ReasoningOutput, ref fields);
        var total = ReadTokenField(element, "total_tokens", TokenUsageFields.Total, ref fields);
        return new TokenCounterSample(
            new TokenBreakdown(input, cachedInput, output, reasoning, total),
            fields);
    }

    private static long ReadTokenField(
        JsonElement element,
        string propertyName,
        TokenUsageFields field,
        ref TokenUsageFields fields)
    {
        if (!TryGetLong(element, propertyName, out var value))
        {
            return 0;
        }

        fields |= field;
        return value;
    }

    private static bool TryReadRateLimitSample(
        JsonElement payload,
        DateTimeOffset timestamp,
        out SessionRateLimitSample sample)
    {
        sample = default!;
        JsonElement limits;
        if (payload.TryGetProperty("info", out var info)
            && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("rate_limits", out limits))
        {
            // Current rollout token_count shape.
        }
        else if (!payload.TryGetProperty("rate_limits", out limits)
                 && !payload.TryGetProperty("rateLimits", out limits))
        {
            return false;
        }

        if (limits.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var namedPrimary = ReadRateLimitWindow(limits, "primary");
        var namedSecondary = ReadRateLimitWindow(limits, "secondary");
        var planType = GetString(limits, "plan_type") ?? GetString(limits, "planType");
        if (namedPrimary is null && namedSecondary is null && string.IsNullOrWhiteSpace(planType))
        {
            return false;
        }

        var (primary, secondary) = RateLimitWindowClassifier.Classify(namedPrimary, namedSecondary);

        sample = new SessionRateLimitSample(
            primary,
            primary is null ? null : timestamp,
            secondary,
            secondary is null ? null : timestamp,
            planType,
            string.IsNullOrWhiteSpace(planType) ? null : timestamp);
        return true;
    }

    private static SessionRateLimitSample MergeRateLimitSamples(
        SessionRateLimitSample? current,
        SessionRateLimitSample candidate)
    {
        if (current is null)
        {
            return candidate;
        }

        var primaryIsNewer = candidate.PrimaryTimestamp is { } candidatePrimaryTimestamp
            && (current.PrimaryTimestamp is null || candidatePrimaryTimestamp >= current.PrimaryTimestamp);
        var secondaryIsNewer = candidate.SecondaryTimestamp is { } candidateSecondaryTimestamp
            && (current.SecondaryTimestamp is null || candidateSecondaryTimestamp >= current.SecondaryTimestamp);
        var planIsNewer = candidate.PlanTimestamp is { } candidatePlanTimestamp
            && (current.PlanTimestamp is null || candidatePlanTimestamp >= current.PlanTimestamp);

        return new SessionRateLimitSample(
            primaryIsNewer ? candidate.Primary : current.Primary,
            primaryIsNewer ? candidate.PrimaryTimestamp : current.PrimaryTimestamp,
            secondaryIsNewer ? candidate.Secondary : current.Secondary,
            secondaryIsNewer ? candidate.SecondaryTimestamp : current.SecondaryTimestamp,
            planIsNewer ? candidate.PlanType : current.PlanType,
            planIsNewer ? candidate.PlanTimestamp : current.PlanTimestamp);
    }

    private static RateLimitWindow? ReadRateLimitWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetDouble(value, "used_percent", out var used)
            && !TryGetDouble(value, "usedPercent", out used))
        {
            return null;
        }

        if (!double.IsFinite(used))
        {
            return null;
        }

        var duration = TryGetInt(value, "window_minutes", out var snakeDuration)
            ? snakeDuration
            : TryGetInt(value, "windowDurationMins", out var camelDuration) ? camelDuration : (int?)null;
        var resetValue = TryGetLong(value, "resets_at", out var snakeReset)
            ? snakeReset
            : TryGetLong(value, "resetsAt", out var camelReset) ? camelReset : (long?)null;
        return new RateLimitWindow(Math.Clamp(used, 0d, 100d), duration, UsageCredits.FromUnixTime(resetValue));
    }

    private static bool TryReadToolName(JsonElement payload, out string toolName)
    {
        toolName = string.Empty;
        var type = GetString(payload, "type") ?? string.Empty;
        if (!type.Contains("call", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("tool_use", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        toolName = GetString(payload, "name") ?? GetString(payload, "tool_name") ?? string.Empty;
        return !string.IsNullOrWhiteSpace(toolName);
    }

    private static bool TryReadSkillName(JsonElement payload, out string skillName)
    {
        skillName = string.Empty;
        if (!payload.TryGetProperty("arguments", out var arguments))
        {
            return false;
        }

        try
        {
            JsonElement argumentRoot;
            JsonDocument? nested = null;
            if (arguments.ValueKind == JsonValueKind.String)
            {
                nested = JsonDocument.Parse(arguments.GetString() ?? "{}");
                argumentRoot = nested.RootElement;
            }
            else
            {
                argumentRoot = arguments;
            }

            using (nested)
            {
                skillName = GetString(argumentRoot, "skill") ?? GetString(argumentRoot, "name") ?? string.Empty;
            }

            return !string.IsNullOrWhiteSpace(skillName);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static bool TryGetInt(JsonElement element, string propertyName, out int result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.TryGetInt32(out result);
    }

    private static bool TryGetLong(JsonElement element, string propertyName, out long result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.TryGetInt64(out result);
    }

    private static bool TryGetDouble(JsonElement element, string propertyName, out double result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var value)
            && value.TryGetDouble(out result);
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
