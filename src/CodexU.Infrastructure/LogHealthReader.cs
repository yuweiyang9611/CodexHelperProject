namespace CodexU.Infrastructure;

public sealed class LogHealthReader(CodexPaths paths)
{
    private const int MaximumBytesPerLog = 1024 * 1024;

    public string ReadDiagnostic()
    {
        var candidates = new[]
        {
            Path.Combine(paths.CodexDirectory, "logs"),
            Path.Combine(paths.CodexDirectory, "log")
        };
        var directory = candidates.FirstOrDefault(Directory.Exists);
        if (directory is null)
        {
            return "日志健康：未找到日志目录";
        }

        var warnings = 0;
        var errors = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.log", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(info => info.LastWriteTimeUtc)
                         .Take(20))
            {
                CountRecentLines(file.FullName, ref warnings, ref errors);
            }

            return $"日志健康：WARN {warnings:N0} · ERROR {errors:N0}（最近 20 个日志，每个最多 1 MiB）";
        }
        catch (IOException ex)
        {
            return $"日志健康：读取失败 {ex.Message}";
        }
        catch (UnauthorizedAccessException)
        {
            return "日志健康：无权读取日志目录";
        }
    }

    private static void CountRecentLines(string path, ref int warnings, ref int errors)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaximumBytesPerLog);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        if (start > 0)
        {
            // The sample can start in the middle of a UTF-8 line. Discard that fragment.
            _ = reader.ReadLine();
        }

        while (reader.ReadLine() is { } line)
        {
            if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                errors++;
            }
            else if (line.Contains("WARN", StringComparison.OrdinalIgnoreCase))
            {
                warnings++;
            }
        }
    }
}
