namespace CodexU.Infrastructure;

internal static class CodexExecutableLocator
{
    private static readonly string[] CommandNames = ["codex.exe", "codex.cmd", "codex.bat", "codex"];

    internal static IReadOnlyList<string> FindCandidates(string? configuredExecutable)
    {
        return FindCandidates(
            configuredExecutable,
            Environment.GetEnvironmentVariable("CODEXU_CODEX_PATH"),
            Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH"));
    }

    internal static IReadOnlyList<string> FindCandidates(
        string? configuredExecutable,
        string? environmentExecutable,
        string? installDirectory,
        string? localApplicationData,
        string? pathValue)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddExistingFile(configuredExecutable, candidates, seen);
        AddExistingFile(environmentExecutable, candidates, seen);
        AddCommandDirectory(installDirectory, candidates, seen);

        if (!string.IsNullOrWhiteSpace(localApplicationData))
        {
            // The standalone Windows installer uses this stable default location.
            AddCommandDirectory(
                Path.Combine(localApplicationData, "Programs", "OpenAI", "Codex", "bin"),
                candidates,
                seen);

            // Current ChatGPT desktop builds keep the runnable Codex CLI in a
            // versioned/hash directory. The MSIX copy under Program Files\WindowsApps
            // can exist but still reject direct launches from an unpackaged process.
            AddVersionedCommandDirectories(
                Path.Combine(localApplicationData, "OpenAI", "Codex", "bin"),
                candidates,
                seen);

            // Keep discovery working if the renamed desktop app later migrates its
            // local runtime directory from Codex to ChatGPT.
            AddVersionedCommandDirectories(
                Path.Combine(localApplicationData, "OpenAI", "ChatGPT", "bin"),
                candidates,
                seen);
        }

        AddPathDirectories(pathValue, candidates, seen);
        return candidates;
    }

    private static void AddPathDirectories(
        string? pathValue,
        ICollection<string> candidates,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return;
        }

        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddCommandDirectory(directory.Trim('"'), candidates, seen);
        }
    }

    private static void AddCommandDirectory(
        string? directory,
        ICollection<string> candidates,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var trimmed = directory.Trim().Trim('"');
        AddExistingFile(trimmed, candidates, seen);

        foreach (var commandName in CommandNames)
        {
            try
            {
                AddExistingFile(Path.Combine(trimmed, commandName), candidates, seen);
            }
            catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException)
            {
                return;
            }
        }
    }

    private static void AddVersionedCommandDirectories(
        string root,
        ICollection<string> candidates,
        ISet<string> seen)
    {
        try
        {
            var versionedCandidates = Directory
                .EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .SelectMany(directory => CommandNames.Select(commandName => Path.Combine(directory, commandName)))
                .Where(File.Exists)
                .OrderByDescending(GetLastWriteTimeUtcSafe)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Try the newest hashed runtime first, then the stable root copy before
            // walking older hashes. The app-server client has one bounded startup
            // budget for the whole list, so placing every stale hash before the root
            // can make the fallback unreachable.
            foreach (var candidate in versionedCandidates.Take(1))
            {
                AddExistingFile(candidate, candidates, seen);
            }

            AddCommandDirectory(root, candidates, seen);

            foreach (var candidate in versionedCandidates.Skip(1))
            {
                AddExistingFile(candidate, candidates, seen);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            // Discovery is best effort. A locked or malformed cache must not block
            // configured paths, the standalone installation, or PATH fallbacks.
        }

        // Enumeration is best effort. If it failed before the root candidate was
        // added, keep the stable copy available as a fallback.
        AddCommandDirectory(root, candidates, seen);
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException)
        {
            return DateTime.MinValue;
        }
    }

    private static void AddExistingFile(
        string? path,
        ICollection<string> candidates,
        ISet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(fullPath) && seen.Add(fullPath))
            {
                candidates.Add(fullPath);
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or System.Security.SecurityException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            // Ignore malformed or inaccessible discovery candidates.
        }
    }
}
