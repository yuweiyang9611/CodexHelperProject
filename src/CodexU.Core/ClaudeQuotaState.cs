namespace CodexU.Core;

public enum ClaudeQuotaSource { None, DesktopQuery, DesktopHistory, StatusLine, CliQuery }

public enum ClaudeTranscriptStatus { Fresh, NoWindows, MissingResult, TooLarge, Stale, Unavailable, Unbound }

public enum ClaudeQuotaAction { None, Query, Rebind }

/// <summary>
/// Outcome of the opt-in background query through the official Claude CLI.
/// NotAvailable means the CLI answered but its login has no plan windows
/// (logged out, API key or a third-party provider); Unavailable covers timeouts,
/// process failures and responses that could not be trusted.
/// </summary>
public enum ClaudeCliQuotaStatus { Disabled, Fresh, NoWindows, NotAvailable, NotInstalled, Unavailable }

public sealed record ClaudeQuotaState(
    ClaudeQuotaSource Source,
    ClaudeTranscriptStatus QueryStatus,
    bool HistoryExpired = false,
    ClaudeCliQuotaStatus CliStatus = ClaudeCliQuotaStatus.Disabled)
{
    // A fresh background CLI reading already carries real reset times, so a
    // stale or missing Desktop transcript result needs no manual re-query.
    public ClaudeQuotaAction Action => Source == ClaudeQuotaSource.CliQuery ? ClaudeQuotaAction.None : QueryStatus switch
    {
        ClaudeTranscriptStatus.TooLarge => ClaudeQuotaAction.Rebind,
        ClaudeTranscriptStatus.MissingResult or ClaudeTranscriptStatus.Stale
            or ClaudeTranscriptStatus.Unavailable or ClaudeTranscriptStatus.NoWindows => ClaudeQuotaAction.Query,
        _ => ClaudeQuotaAction.None
    };
}
