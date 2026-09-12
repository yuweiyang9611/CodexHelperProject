namespace CodexU.Infrastructure;

internal static class WorkspaceScope
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim())); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static bool Contains(string? workspace, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return true;
        var root = Normalize(workspace);
        var path = Normalize(candidate);
        if (root is null || path is null) return false;
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
