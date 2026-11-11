namespace CodexU.Core;

public enum ClaudeQuotaSource { None, DesktopQuery, DesktopHistory, StatusLine }

public enum ClaudeTranscriptStatus { Fresh, NoWindows, MissingResult, TooLarge, Stale, Unavailable, Unbound }

public enum ClaudeQuotaAction { None, Query, Rebind }

public sealed record ClaudeQuotaState(ClaudeQuotaSource Source, ClaudeTranscriptStatus QueryStatus, bool HistoryExpired = false)
{
    public ClaudeQuotaAction Action => QueryStatus switch
    {
        ClaudeTranscriptStatus.TooLarge => ClaudeQuotaAction.Rebind,
        ClaudeTranscriptStatus.MissingResult or ClaudeTranscriptStatus.Stale
            or ClaudeTranscriptStatus.Unavailable or ClaudeTranscriptStatus.NoWindows => ClaudeQuotaAction.Query,
        _ => ClaudeQuotaAction.None
    };
}
