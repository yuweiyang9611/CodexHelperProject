namespace CodexU.Core;

/// <summary>One reading of a rate-limit window, taken at a known moment.</summary>
public sealed record QuotaSample(
    DateTimeOffset ObservedAt,
    double UsedPercent,
    DateTimeOffset? ResetsAt);

public sealed record QuotaForecastOptions(
    TimeSpan Lookback,
    TimeSpan MinimumSpan,
    double ResetDropPercent)
{
    /// <summary>
    /// Lookback is deliberately much longer than a refresh interval: the rate is
    /// measured against the oldest reading still in the window, not against the
    /// previous one. Anchoring on the previous reading would tie the requirement to
    /// the refresh cadence — at the default five-minute refresh a fifteen-minute
    /// minimum could never be met and the forecast would never appear — and it would
    /// let a single busy interval dominate the slope.
    /// </summary>
    public static QuotaForecastOptions Default { get; } = new(
        Lookback: TimeSpan.FromMinutes(90),
        MinimumSpan: TimeSpan.FromMinutes(15),
        ResetDropPercent: 5d);
}

public sealed record QuotaForecast(
    double PercentPerMinute,
    TimeSpan TimeToExhaustion,
    DateTimeOffset ExhaustsAt,
    bool ExhaustsBeforeReset,
    TimeSpan MeasuredOver);

public static class QuotaForecastAlert
{
    /// <summary>
    /// A five-hour window needs about an hour's warning to be worth acting on; a
    /// weekly one is worth flagging a day ahead, since rearranging a week's work is
    /// not a five-minute decision.
    /// </summary>
    public static TimeSpan PrimaryLeadTime { get; } = TimeSpan.FromHours(1);

    public static TimeSpan SecondaryLeadTime { get; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Decides whether a projection is worth interrupting the user for.
    ///
    /// It has to clear three bars: the window must actually run out before it resets,
    /// it must do so soon enough to act on, and the plain remaining-percent alert must
    /// not have fired already — below that threshold the user has been told, and a
    /// second balloon restating it in different words is just noise.
    /// </summary>
    public static bool ShouldWarn(
        RateLimitWindow? quota,
        QuotaForecast? forecast,
        int alertThresholdPercent,
        TimeSpan leadTime) =>
        quota is not null
        && forecast is not null
        && forecast.ExhaustsBeforeReset
        && forecast.TimeToExhaustion <= leadTime
        && quota.RemainingPercent > alertThresholdPercent;
}

public static class QuotaForecaster
{
    /// <summary>
    /// Estimates when a rate-limit window runs out at the pace it has actually been
    /// consumed. Returns null whenever the answer would be guesswork rather than a
    /// measurement — too little history, a window that just reset, or usage that is
    /// flat or falling.
    /// </summary>
    public static QuotaForecast? Project(
        RateLimitWindow? current,
        IReadOnlyList<QuotaSample> samples,
        DateTimeOffset now,
        QuotaForecastOptions? options = null)
    {
        if (current is null || samples.Count < 2)
        {
            return null;
        }

        var settings = options ?? QuotaForecastOptions.Default;
        var window = samples
            .Where(sample => sample.ObservedAt <= now && sample.ObservedAt >= now - settings.Lookback)
            .OrderBy(sample => sample.ObservedAt)
            .ToArray();
        if (window.Length < 2)
        {
            return null;
        }

        var newest = window[^1];
        var anchor = FindAnchor(window, settings.ResetDropPercent);
        var measuredOver = newest.ObservedAt - anchor.ObservedAt;
        if (measuredOver < settings.MinimumSpan)
        {
            return null;
        }

        var consumed = newest.UsedPercent - anchor.UsedPercent;
        var percentPerMinute = consumed / measuredOver.TotalMinutes;
        if (percentPerMinute <= 0 || !double.IsFinite(percentPerMinute))
        {
            // Flat or falling usage never runs out.
            return null;
        }

        var remaining = 100d - current.UsedPercent;
        if (remaining <= 0)
        {
            // Already exhausted; there is nothing left to predict.
            return null;
        }

        var minutes = remaining / percentPerMinute;
        if (!double.IsFinite(minutes) || minutes > TimeSpan.MaxValue.TotalMinutes)
        {
            return null;
        }

        var timeToExhaustion = TimeSpan.FromMinutes(minutes);
        var exhaustsAt = now + timeToExhaustion;

        // A window that resets before it would be exhausted is not a problem worth
        // warning about, however fast it is being consumed.
        var exhaustsBeforeReset = current.ResetsAt is null || exhaustsAt < current.ResetsAt;

        return new QuotaForecast(
            percentPerMinute,
            timeToExhaustion,
            exhaustsAt,
            exhaustsBeforeReset,
            measuredOver);
    }

    /// <summary>
    /// Walks back from the newest reading to the oldest one still belonging to the
    /// same window instance. Usage only climbs inside a window, so a sizeable fall
    /// between consecutive readings marks a reset — that is more dependable than
    /// comparing reset timestamps, which can move while a window is live.
    /// </summary>
    private static QuotaSample FindAnchor(IReadOnlyList<QuotaSample> ascending, double resetDropPercent)
    {
        var anchor = ascending[^1];
        for (var index = ascending.Count - 1; index > 0; index--)
        {
            var earlier = ascending[index - 1];
            if (ascending[index].UsedPercent + resetDropPercent < earlier.UsedPercent)
            {
                break;
            }

            anchor = earlier;
        }

        return anchor;
    }
}
