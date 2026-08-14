using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Turns the rate-limit readings of one refresh into a burn-rate projection, by
/// keeping a trail of past readings alongside them.
/// </summary>
public sealed class QuotaForecastCollector(QuotaSampleStore store, QuotaForecastOptions? options = null)
{
    /// <summary>Codex reads a window from the app server when it can, from local rollouts otherwise.</summary>
    public const string AppServerSource = "app-server";
    public const string LocalSource = "local";
    public const string StatuslineSource = "statusline";

    private const string PrimaryWindow = "five-hour";
    private const string SecondaryWindow = "seven-day";

    /// <summary>
    /// Comfortably longer than the lookback so a trail survives the app being closed
    /// and reopened; the per-window sample cap bounds the cost either way.
    /// </summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private readonly QuotaForecastOptions _options = options ?? QuotaForecastOptions.Default;

    public Task<(QuotaForecast? Primary, QuotaForecast? Secondary)> RecordAsync(
        AgentRuntime runtime,
        QuotaReading primary,
        QuotaReading secondary,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        store.UpdateAsync(
            trails => (
                Project(trails, runtime, PrimaryWindow, primary, now),
                Project(trails, runtime, SecondaryWindow, secondary, now)),
            now,
            Retention,
            cancellationToken);

    private QuotaForecast? Project(
        IDictionary<string, IReadOnlyList<QuotaSample>> trails,
        AgentRuntime runtime,
        string window,
        QuotaReading reading,
        DateTimeOffset now)
    {
        if (reading.Window is null || reading.Source is null)
        {
            // Nothing read this time. The trail stays as it is rather than being
            // cleared: a source that blinks out for one refresh should not throw away
            // the history that makes the next forecast possible.
            return null;
        }

        var key = QuotaSampleStore.WindowKey(runtime, window, reading.Source);
        var existing = trails.TryGetValue(key, out var trail) ? trail : [];

        // A reading dated in the future — a clock skew between the writer and this
        // process — would otherwise be dropped by the forecaster's lookback filter
        // and stall the trail permanently.
        var observedAt = reading.Window.MeasuredAt is { } measured && measured <= now ? measured : now;
        var updated = QuotaSampleStore.Append(
            existing,
            new QuotaSample(observedAt, reading.Window.UsedPercent, reading.Window.ResetsAt));
        trails[key] = updated;

        return QuotaForecaster.Project(reading.Window, updated, now, _options);
    }
}

/// <summary>A rate-limit window together with where it was read from.</summary>
public readonly record struct QuotaReading(RateLimitWindow? Window, string? Source)
{
    public static QuotaReading None => new(null, null);
}
