namespace CodexU.Core;

public enum ClaudeCliLoginStatus { LoggedIn, LoggedOut, NotInstalled, Unavailable }

public enum ClaudeCliAuthMethod { None, ClaudeAi, OauthToken, ApiKey, ApiKeyHelper, ThirdParty, Unknown }

public enum ClaudeCliSource { None, Standalone, Path, DesktopEmbedded }

public sealed record ClaudeCliAuthStatus(
    ClaudeCliLoginStatus Status,
    ClaudeCliAuthMethod AuthMethod,
    ClaudeCliSource Source,
    DateTimeOffset CheckedAt);
