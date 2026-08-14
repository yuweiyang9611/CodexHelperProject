namespace CodexU.Infrastructure;

public sealed record CodexPaths(
    string HomeDirectory,
    string CodexDirectory,
    string StateDatabase,
    string SessionsDirectory,
    string ArchivedSessionsDirectory,
    string AutomationsDirectory,
    string GoalsDatabase,
    string ClaudeDirectory)
{
    public static CodexPaths Discover(string? configuredCodexHome = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
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
            Path.Combine(home, ".claude"));
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
