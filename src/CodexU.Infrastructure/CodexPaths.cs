namespace CodexU.Infrastructure;

public sealed record CodexPaths(
    string HomeDirectory,
    string CodexDirectory,
    string StateDatabase,
    string SessionsDirectory,
    string ArchivedSessionsDirectory,
    string AutomationsDirectory,
    string GoalsDatabase,
    string ClaudeDirectory,
    string? ClaudeDesktopDataDirectory = null)
{
    public static CodexPaths Discover(string? configuredCodexHome = null) => Discover(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        configuredCodexHome,
        Environment.GetEnvironmentVariable("CODEX_HOME"),
        Environment.GetEnvironmentVariable("CODEXU_CLAUDE_DIRECTORY"));

    internal static CodexPaths Discover(string home, string localApplicationData,
        string? configuredCodexHome, string? environmentHome, string? environmentClaudeDirectory)
    {
        var fallback = Path.Combine(home, ".codex");
        var codex = TryNormalize(configuredCodexHome)
            ?? TryNormalize(environmentHome)
            ?? fallback;

        return new CodexPaths(
            home,
            codex,
            Path.Combine(codex, "state_5.sqlite"),
            Path.Combine(codex, "sessions"),
            Path.Combine(codex, "archived_sessions"),
            Path.Combine(codex, "automations"),
            Path.Combine(codex, "goals_1.sqlite"),
            TryNormalize(environmentClaudeDirectory) ?? Path.Combine(home, ".claude"),
            // An explicit Code source can belong to another account or an isolated
            // smoke test. Never silently mix in this user's Desktop account then.
            string.IsNullOrWhiteSpace(environmentClaudeDirectory) ? Path.Combine(localApplicationData, "Claude-Data") : null);
    }

    private static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
