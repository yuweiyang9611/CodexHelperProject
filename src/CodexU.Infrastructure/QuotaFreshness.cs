using CodexU.Core;

namespace CodexU.Infrastructure;

internal static class QuotaFreshness
{
    public static RateLimitWindow? KeepCurrent(
        RateLimitWindow? quota,
        DateTimeOffset? sampledAt,
        DateTimeOffset now)
    {
        if (quota is null)
        {
            return null;
        }

        // An explicit reset is authoritative even when the sample timestamp is
        // old or missing.
        if (quota.ResetsAt is { } reset)
        {
            return reset > now ? quota : null;
        }

        if (sampledAt is not { } timestamp || quota.WindowDurationMinutes is not > 0)
        {
            return quota;
        }

        try
        {
            return timestamp.AddMinutes(quota.WindowDurationMinutes.Value) > now ? quota : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            // A nonsensically large duration cannot be proven stale. Preserve it
            // instead of hiding a potentially valid quota.
            return quota;
        }
    }
}
