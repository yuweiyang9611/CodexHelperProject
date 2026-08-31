namespace CodexU.Core;

/// <summary>
/// Resolves the identity of one quota-window instance. Implementations may keep
/// state so hosts can preserve that identity across process restarts even when a
/// runtime does not report a reset timestamp.
/// </summary>
public interface IQuotaWindowIdentityProvider
{
    /// <summary>
    /// Observes the latest reading and returns a stable, non-sensitive logical key
    /// for the current window instance.
    /// </summary>
    string ObserveWindow(
        AgentRuntime runtime,
        string label,
        RateLimitWindow quota,
        DateTimeOffset observedAt);
}

/// <summary>
/// Stateless fallback used by hosts that do not supply durable window state.
/// Reset timestamps are exact identities; otherwise a duration-sized UTC bucket
/// keeps IDs stable across short-lived process restarts.
/// </summary>
internal sealed class TimeBucketQuotaWindowIdentityProvider : IQuotaWindowIdentityProvider
{
    public string ObserveWindow(
        AgentRuntime runtime,
        string label,
        RateLimitWindow quota,
        DateTimeOffset observedAt)
    {
        if (quota.ResetsAt is { } reset)
        {
            return $"{runtime}|{label}|reset:{reset.UtcDateTime.Ticks}";
        }

        var durationMinutes = quota.WindowDurationMinutes is > 0
            ? quota.WindowDurationMinutes.Value
            : string.Equals(label, "7 天", StringComparison.Ordinal) ? 7 * 24 * 60 : 5 * 60;
        var durationTicks = TimeSpan.FromMinutes(durationMinutes).Ticks;
        var bucket = observedAt.UtcDateTime.Ticks / durationTicks;
        return $"{runtime}|{label}|bucket:{durationMinutes}:{bucket}";
    }
}
