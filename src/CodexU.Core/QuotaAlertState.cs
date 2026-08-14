namespace CodexU.Core;

public enum QuotaRefreshKind
{
    /// <summary>Usage dropped sharply — the allowance genuinely rolled over.</summary>
    Refreshed,

    /// <summary>The reset time moved but usage did not fall; only the schedule changed.</summary>
    RescheduleOnly,
}

/// <summary>Which tray alert a window can raise. Each fires once per window instance.</summary>
public enum QuotaAlertKind
{
    /// <summary>Remaining allowance fell below the configured threshold.</summary>
    BelowThreshold,

    /// <summary>The measured pace projects the window running out before it resets.</summary>
    Forecast,
}

/// <summary>
/// Remembers which quota alerts have already fired, per runtime and per window.
///
/// Every piece of state here has to be keyed by runtime. The two vendors' windows
/// have independent reset clocks and independent allowances, so state shared between
/// them makes one runtime's activity look like the other's — a switch between them
/// reads as a quota reset, and an alert for one silences the other.
/// </summary>
public sealed class QuotaAlertState
{
    /// <summary>
    /// A rise this large in remaining allowance marks a new window instance. Usage
    /// only climbs inside a window, so a sizeable fall in used percent — equivalently
    /// a rise in remaining — is a rollover. This is the same signal the burn-rate
    /// forecaster uses, and it needs no reset timestamp: Claude's snapshot often omits
    /// one, and keying on it collapsed every future window onto a single key that
    /// silenced the runtime for the rest of the session.
    /// </summary>
    public const double WindowRolloverRisePercent = 5d;

    private readonly Dictionary<string, double> _lastRemaining = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fired = new(StringComparer.Ordinal);
    private readonly Dictionary<AgentRuntime, (DateTimeOffset Reset, double Used)> _lastPrimary = [];

    /// <summary>
    /// Records the current reading for a window and re-arms its alerts when the window
    /// has rolled over. Call once per window per refresh, before asking whether to
    /// announce anything.
    /// </summary>
    public void ObserveWindow(AgentRuntime runtime, string window, RateLimitWindow? quota)
    {
        if (quota is null)
        {
            return;
        }

        var key = $"{runtime}|{window}";
        if (_lastRemaining.TryGetValue(key, out var previous)
            && quota.RemainingPercent > previous + WindowRolloverRisePercent)
        {
            foreach (var kind in Enum.GetValues<QuotaAlertKind>())
            {
                _fired.Remove($"{kind}|{key}");
            }
        }

        _lastRemaining[key] = quota.RemainingPercent;
    }

    /// <summary>
    /// True the first time a condition holds for a given window instance, false on every
    /// later refresh while it still holds — so the user is told once rather than at every
    /// refresh for as long as the situation lasts.
    /// </summary>
    public bool TryAnnounce(AgentRuntime runtime, string window, QuotaAlertKind kind, bool conditionHolds) =>
        conditionHolds && _fired.Add($"{kind}|{runtime}|{window}");

    /// <summary>
    /// Reports a change in the primary window's reset time, or null when nothing has
    /// changed. State is per runtime: the two runtimes' reset clocks are unrelated, so a
    /// shared record turns every runtime switch into a spurious "quota refreshed" balloon
    /// — and, worse, compares one vendor's used percent against the other's, announcing a
    /// reset that never happened.
    /// </summary>
    public QuotaRefreshKind? ObserveReset(AgentRuntime runtime, RateLimitWindow? quota)
    {
        if (quota?.ResetsAt is not { } nextReset)
        {
            return null;
        }

        QuotaRefreshKind? kind = null;
        if (_lastPrimary.TryGetValue(runtime, out var previous) && previous.Reset != nextReset)
        {
            kind = quota.UsedPercent + WindowRolloverRisePercent < previous.Used
                ? QuotaRefreshKind.Refreshed
                : QuotaRefreshKind.RescheduleOnly;
        }

        _lastPrimary[runtime] = (nextReset, quota.UsedPercent);
        return kind;
    }
}
