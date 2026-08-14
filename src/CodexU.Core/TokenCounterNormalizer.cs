namespace CodexU.Core;

[Flags]
public enum TokenUsageFields
{
    None = 0,
    Input = 1 << 0,
    CachedInput = 1 << 1,
    Output = 1 << 2,
    ReasoningOutput = 1 << 3,
    Total = 1 << 4
}

/// <summary>
/// A token counter together with the fields that were actually present in JSON.
/// Missing cumulative fields must not be interpreted as counter resets to zero.
/// </summary>
public sealed record TokenCounterSample(TokenBreakdown Tokens, TokenUsageFields Fields)
{
    public bool Has(TokenUsageFields field) => (Fields & field) == field;
}

/// <summary>
/// Serializable state required to resume normalization at an incremental file offset.
/// High-water values prevent an auxiliary counter rollback from being counted again.
/// </summary>
public sealed record TokenCounterState(
    TokenBreakdown LastCumulative,
    TokenUsageFields LastCumulativeFields,
    TokenBreakdown HighWater,
    TokenUsageFields HighWaterFields,
    int Epoch,
    bool HasSamples)
{
    public static TokenCounterState Empty { get; } = new(
        TokenBreakdown.Zero,
        TokenUsageFields.None,
        TokenBreakdown.Zero,
        TokenUsageFields.None,
        0,
        false);
}

public sealed record TokenCounterObservation(TokenBreakdown Delta, TokenCounterState State);

/// <summary>
/// Converts Codex token counters into per-response usage. When a cumulative sample
/// exists, last usage is trusted only after that counter advances or resets because
/// repeated notifications can retain the prior non-zero last_token_usage. A valid
/// last-only sample remains the best evidence when cumulative data is absent.
/// </summary>
public static class TokenCounterNormalizer
{
    public static TokenCounterObservation Observe(
        TokenCounterState state,
        TokenCounterSample? cumulative,
        TokenCounterSample? last)
    {
        ArgumentNullException.ThrowIfNull(state);
        cumulative = Sanitize(cumulative);
        last = Sanitize(last);

        if (cumulative is null || cumulative.Fields == TokenUsageFields.None)
        {
            return new TokenCounterObservation(LastOnly(last), state);
        }

        var isReset = state.HasSamples && IsConfirmedReset(state, cumulative);
        var advanced = !state.HasSamples || isReset || HasAdvanced(state, cumulative);
        var fallback = isReset
            ? Clamp(cumulative.Tokens)
            : DifferenceFromHighWater(state, cumulative);
        var delta = advanced
            ? PreferLast(last, fallback)
            : TokenBreakdown.Zero;

        var nextState = isReset
            ? new TokenCounterState(
                Clamp(cumulative.Tokens),
                cumulative.Fields,
                Clamp(cumulative.Tokens),
                cumulative.Fields,
                state.Epoch + 1,
                true)
            : new TokenCounterState(
                Clamp(cumulative.Tokens),
                cumulative.Fields,
                MergeHighWater(state.HighWater, state.HighWaterFields, cumulative),
                state.HighWaterFields | cumulative.Fields,
                state.Epoch,
                true);

        return new TokenCounterObservation(Clamp(delta), nextState);
    }

    private static bool HasAdvanced(TokenCounterState state, TokenCounterSample current)
    {
        if (TryComparableTotal(current.Tokens, current.Fields, out var currentTotal)
            && TryComparableTotal(state.HighWater, state.HighWaterFields, out var previousTotal))
        {
            if (currentTotal > previousTotal)
            {
                return true;
            }

            // A repeated notification has no cumulative field above its high-water.
            // If a malformed/auxiliary total stalls or rolls back while a primary
            // slice advances, the advancing slice still proves that this is a new
            // response and last_token_usage remains the best delta.
            return IsAboveHighWater(current, state, TokenUsageFields.Input)
                   || IsAboveHighWater(current, state, TokenUsageFields.Output);
        }

        return IsAboveHighWater(current, state, TokenUsageFields.Total)
               || IsAboveHighWater(current, state, TokenUsageFields.Input)
               || IsAboveHighWater(current, state, TokenUsageFields.Output);
    }

    private static bool IsConfirmedReset(TokenCounterState state, TokenCounterSample current)
    {
        if (!TryComparableTotal(current.Tokens, current.Fields, out var currentTotal)
            || !TryComparableTotal(state.HighWater, state.HighWaterFields, out var previousTotal)
            || currentTotal >= previousTotal)
        {
            return false;
        }

        // Confirm a new epoch with the cumulative input counter as the independent
        // second signal. A malformed total rollback alone must not reset high-water.
        if ((state.HighWaterFields & TokenUsageFields.Input) == 0
            || !current.Has(TokenUsageFields.Input))
        {
            return false;
        }

        return NonNegative(current.Tokens.InputTokens) < NonNegative(state.HighWater.InputTokens);
    }

    private static bool HasPrimarySlices(TokenUsageFields fields) =>
        (fields & (TokenUsageFields.Input | TokenUsageFields.Output))
        == (TokenUsageFields.Input | TokenUsageFields.Output);

    private static bool TryComparableTotal(
        TokenBreakdown tokens,
        TokenUsageFields fields,
        out long total)
    {
        if ((fields & TokenUsageFields.Total) != 0)
        {
            total = NonNegative(tokens.TotalTokens);
            return true;
        }

        if (HasPrimarySlices(fields))
        {
            total = NonNegative(tokens.InputTokens) + NonNegative(tokens.OutputTokens);
            return true;
        }

        total = 0;
        return false;
    }

    private static bool IsAboveHighWater(
        TokenCounterSample current,
        TokenCounterState state,
        TokenUsageFields field)
    {
        if (!current.Has(field))
        {
            return false;
        }

        if ((state.HighWaterFields & field) == 0)
        {
            // A cumulative field first appearing mid-stream is an unknown
            // baseline, not independent evidence of a new response.
            return false;
        }

        return Value(current.Tokens, field) > Value(state.HighWater, field);
    }

    private static TokenBreakdown PreferLast(TokenCounterSample? last, TokenBreakdown fallback)
    {
        if (last is null || last.Fields == TokenUsageFields.None)
        {
            return fallback;
        }

        return new TokenBreakdown(
            Choose(last, TokenUsageFields.Input, fallback.InputTokens),
            Choose(last, TokenUsageFields.CachedInput, fallback.CachedInputTokens),
            Choose(last, TokenUsageFields.Output, fallback.OutputTokens),
            Choose(last, TokenUsageFields.ReasoningOutput, fallback.ReasoningOutputTokens),
            Choose(last, TokenUsageFields.Total, fallback.TotalTokens));
    }

    private static TokenBreakdown LastOnly(TokenCounterSample? last) => last is null
        ? TokenBreakdown.Zero
        : new TokenBreakdown(
            Choose(last, TokenUsageFields.Input, 0),
            Choose(last, TokenUsageFields.CachedInput, 0),
            Choose(last, TokenUsageFields.Output, 0),
            Choose(last, TokenUsageFields.ReasoningOutput, 0),
            Choose(last, TokenUsageFields.Total, 0));

    private static long Choose(TokenCounterSample sample, TokenUsageFields field, long fallback)
    {
        if (!sample.Has(field))
        {
            return fallback;
        }

        var value = Value(sample.Tokens, field);
        return value >= 0 ? value : fallback;
    }

    private static TokenBreakdown DifferenceFromHighWater(
        TokenCounterState state,
        TokenCounterSample current) => new(
        Difference(state, current, TokenUsageFields.Input),
        Difference(state, current, TokenUsageFields.CachedInput),
        Difference(state, current, TokenUsageFields.Output),
        Difference(state, current, TokenUsageFields.ReasoningOutput),
        Difference(state, current, TokenUsageFields.Total));

    private static long Difference(
        TokenCounterState state,
        TokenCounterSample current,
        TokenUsageFields field)
    {
        if (!current.Has(field))
        {
            return 0;
        }

        var value = NonNegative(Value(current.Tokens, field));
        if (!state.HasSamples)
        {
            return value;
        }

        if ((state.HighWaterFields & field) != 0)
        {
            return Math.Max(0, value - NonNegative(Value(state.HighWater, field)));
        }

        // A field first appearing mid-stream is an unknown cumulative baseline,
        // not evidence that the whole value was consumed by this response.
        return 0;
    }

    private static TokenBreakdown MergeHighWater(
        TokenBreakdown highWater,
        TokenUsageFields highWaterFields,
        TokenCounterSample current) => new(
        Merge(highWater, highWaterFields, current, TokenUsageFields.Input),
        Merge(highWater, highWaterFields, current, TokenUsageFields.CachedInput),
        Merge(highWater, highWaterFields, current, TokenUsageFields.Output),
        Merge(highWater, highWaterFields, current, TokenUsageFields.ReasoningOutput),
        Merge(highWater, highWaterFields, current, TokenUsageFields.Total));

    private static long Merge(
        TokenBreakdown highWater,
        TokenUsageFields highWaterFields,
        TokenCounterSample current,
        TokenUsageFields field)
    {
        var previous = (highWaterFields & field) != 0 ? NonNegative(Value(highWater, field)) : 0;
        return current.Has(field)
            ? Math.Max(previous, NonNegative(Value(current.Tokens, field)))
            : previous;
    }

    private static long Value(TokenBreakdown tokens, TokenUsageFields field) => field switch
    {
        TokenUsageFields.Input => tokens.InputTokens,
        TokenUsageFields.CachedInput => tokens.CachedInputTokens,
        TokenUsageFields.Output => tokens.OutputTokens,
        TokenUsageFields.ReasoningOutput => tokens.ReasoningOutputTokens,
        TokenUsageFields.Total => tokens.TotalTokens,
        _ => 0
    };

    private static TokenBreakdown Clamp(TokenBreakdown value) => new(
        NonNegative(value.InputTokens),
        NonNegative(value.CachedInputTokens),
        NonNegative(value.OutputTokens),
        NonNegative(value.ReasoningOutputTokens),
        NonNegative(value.TotalTokens),
        NonNegative(value.CacheWrite5mTokens),
        NonNegative(value.CacheWrite1hTokens));

    private static TokenCounterSample? Sanitize(TokenCounterSample? sample)
    {
        if (sample is null)
        {
            return null;
        }

        var fields = sample.Fields;
        fields = RemoveNegative(fields, TokenUsageFields.Input, sample.Tokens.InputTokens);
        fields = RemoveNegative(fields, TokenUsageFields.CachedInput, sample.Tokens.CachedInputTokens);
        fields = RemoveNegative(fields, TokenUsageFields.Output, sample.Tokens.OutputTokens);
        fields = RemoveNegative(fields, TokenUsageFields.ReasoningOutput, sample.Tokens.ReasoningOutputTokens);
        fields = RemoveNegative(fields, TokenUsageFields.Total, sample.Tokens.TotalTokens);
        return new TokenCounterSample(Clamp(sample.Tokens), fields);
    }

    private static TokenUsageFields RemoveNegative(
        TokenUsageFields fields,
        TokenUsageFields field,
        long value) => value < 0 ? fields & ~field : fields;

    private static long NonNegative(long value) => Math.Max(0, value);
}
