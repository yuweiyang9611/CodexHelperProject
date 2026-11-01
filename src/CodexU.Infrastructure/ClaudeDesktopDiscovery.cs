namespace CodexU.Infrastructure;

internal static class ClaudeDesktopDiscovery
{
    internal const string HistoryFileName = "plan-usage-history.json";
    private const int MaximumPackages = 16;

    internal static IReadOnlyList<string> FindHistoryFiles(string? dataDirectory)
    {
        // Only Discover supplies the real Desktop root. An omitted root is an
        // explicit opt-out, including for existing isolated reader fixtures.
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Directory.Exists(dataDirectory)) return [];
        RejectLink(dataDirectory);
        var packages = Directory.EnumerateDirectories(dataDirectory, "claude_*", SearchOption.TopDirectoryOnly)
            .Take(MaximumPackages + 1).ToArray();
        if (packages.Length > MaximumPackages) throw new InvalidDataException("安装目录数量超过安全上限");

        var files = new List<string>();
        foreach (var directory in packages.Order(StringComparer.OrdinalIgnoreCase))
        {
            RejectLink(directory);
            var file = Path.Combine(directory, HistoryFileName);
            if (!File.Exists(file)) continue;
            RejectLink(file);
            files.Add(file);
        }
        return files;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("不读取链接指向的历史文件或目录");
    }
}
