using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class QuotaAlertStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryAnnounce_TellsTheUserOnceWhileTheConditionHolds()
    {
        var state = new QuotaAlertState();

        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
        Assert.False(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void TryAnnounce_KeepsTheTwoRuntimesApart()
    {
        // The bug this replaces: with the runtime out of the key, whichever runtime
        // alerted first permanently silenced the other.
        var state = new QuotaAlertState();

        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
        Assert.True(state.TryAnnounce(AgentRuntime.ClaudeCode, "5 小时", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void TryAnnounce_KeepsTheTwoWindowsAndTheTwoAlertKindsApart()
    {
        var state = new QuotaAlertState();

        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "7 天", QuotaAlertKind.BelowThreshold, true));
        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.Forecast, true));
    }

    [Fact]
    public void TryAnnounce_SaysNothingWhileTheConditionDoesNotHold()
    {
        var state = new QuotaAlertState();

        Assert.False(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, false));
        // Not consumed by the false call — the first time it genuinely holds still alerts.
        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void ObserveWindow_ReArmsAlertsWhenTheWindowRollsOver()
    {
        // Claude's snapshot often omits a reset time, so the old key degenerated to a
        // constant and the runtime stayed silent through every later window for the
        // rest of the session. A rise in remaining allowance is the rollover, and it
        // needs no timestamp.
        var state = new QuotaAlertState();
        state.ObserveWindow(AgentRuntime.ClaudeCode, "5 小时", Window(remainingPercent: 12));
        Assert.True(state.TryAnnounce(AgentRuntime.ClaudeCode, "5 小时", QuotaAlertKind.BelowThreshold, true));

        state.ObserveWindow(AgentRuntime.ClaudeCode, "5 小时", Window(remainingPercent: 96));

        Assert.True(state.TryAnnounce(AgentRuntime.ClaudeCode, "5 小时", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void ObserveWindow_DoesNotReArmOnNoiseWithinTheSameWindow()
    {
        // Remaining only falls inside a window; a small wobble is a measurement
        // artefact, not a rollover, and re-alerting on it would nag.
        var state = new QuotaAlertState();
        state.ObserveWindow(AgentRuntime.Codex, "5 小时", Window(remainingPercent: 12));
        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));

        state.ObserveWindow(AgentRuntime.Codex, "5 小时", Window(remainingPercent: 14));

        Assert.False(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void ObserveWindow_ReArmsOnlyTheWindowThatRolledOver()
    {
        var state = new QuotaAlertState();
        state.ObserveWindow(AgentRuntime.Codex, "5 小时", Window(remainingPercent: 12));
        state.ObserveWindow(AgentRuntime.Codex, "7 天", Window(remainingPercent: 8));
        state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true);
        state.TryAnnounce(AgentRuntime.Codex, "7 天", QuotaAlertKind.BelowThreshold, true);

        state.ObserveWindow(AgentRuntime.Codex, "5 小时", Window(remainingPercent: 99));
        state.ObserveWindow(AgentRuntime.Codex, "7 天", Window(remainingPercent: 7));

        Assert.True(state.TryAnnounce(AgentRuntime.Codex, "5 小时", QuotaAlertKind.BelowThreshold, true));
        Assert.False(state.TryAnnounce(AgentRuntime.Codex, "7 天", QuotaAlertKind.BelowThreshold, true));
    }

    [Fact]
    public void ObserveReset_SaysNothingOnTheFirstReading()
    {
        var state = new QuotaAlertState();

        Assert.Null(state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 60, resetsIn: TimeSpan.FromHours(3))));
    }

    [Fact]
    public void ObserveReset_SeparatesARealRefreshFromAMovedSchedule()
    {
        var state = new QuotaAlertState();
        state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 20, resetsIn: TimeSpan.FromHours(1)));

        // Reset moved and usage fell sharply — the allowance genuinely rolled over.
        Assert.Equal(
            QuotaRefreshKind.Refreshed,
            state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 98, resetsIn: TimeSpan.FromHours(5))));

        // Reset moved again but usage kept climbing — only the schedule changed.
        Assert.Equal(
            QuotaRefreshKind.RescheduleOnly,
            state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 90, resetsIn: TimeSpan.FromHours(6))));
    }

    [Fact]
    public void ObserveReset_DoesNotTreatARuntimeSwitchAsARefresh()
    {
        // The bug this replaces: one shared record meant switching runtimes compared
        // Codex's reset clock and used percent against Claude's, so every switch popped
        // a balloon announcing a reset that never happened — and repeated all session.
        var state = new QuotaAlertState();
        state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 20, resetsIn: TimeSpan.FromHours(1)));

        var onSwitch = state.ObserveReset(AgentRuntime.ClaudeCode, Window(remainingPercent: 90, resetsIn: TimeSpan.FromHours(4)));

        Assert.Null(onSwitch);
        // Switching back is still not a refresh: Codex's own reading is unchanged.
        Assert.Null(state.ObserveReset(AgentRuntime.Codex, Window(remainingPercent: 20, resetsIn: TimeSpan.FromHours(1))));
    }

    [Fact]
    public void ObserveReset_SaysNothingWithoutAResetTime()
    {
        var state = new QuotaAlertState();

        Assert.Null(state.ObserveReset(AgentRuntime.ClaudeCode, Window(remainingPercent: 40)));
        Assert.Null(state.ObserveReset(AgentRuntime.ClaudeCode, null));
    }

    private static RateLimitWindow Window(double remainingPercent, TimeSpan? resetsIn = null) =>
        new(100d - remainingPercent, 300, resetsIn is null ? null : Now + resetsIn.Value);
}
