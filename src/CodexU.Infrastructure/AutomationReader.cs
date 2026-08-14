using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class AutomationReader(CodexPaths paths)
{
    public IReadOnlyList<TaskItem> ReadScheduledTasks()
    {
        if (!Directory.Exists(paths.AutomationsDirectory))
        {
            return [];
        }

        var result = new List<TaskItem>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(paths.AutomationsDirectory, "automation.toml", SearchOption.AllDirectories))
            {
                try
                {
                    var values = ReadSimpleToml(file);
                    if (values.TryGetValue("enabled", out var enabled)
                        && bool.TryParse(enabled, out var isEnabled)
                        && !isEnabled)
                    {
                        continue;
                    }

                    var id = Directory.GetParent(file)?.Name ?? Path.GetFileNameWithoutExtension(file);
                    var title = values.GetValueOrDefault("name")
                        ?? values.GetValueOrDefault("title")
                        ?? "定时任务";
                    var detail = values.GetValueOrDefault("rrule")
                        ?? values.GetValueOrDefault("schedule")
                        ?? values.GetValueOrDefault("cron");
                    result.Add(new TaskItem(
                        $"automation:{id}",
                        title,
                        "Automation",
                        File.GetLastWriteTimeUtc(file),
                        null,
                        TaskColumnKind.Scheduled,
                        detail));
                }
                catch (IOException)
                {
                    // Best effort: a concurrently edited automation is skipped for this refresh.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best effort: inaccessible automation metadata is not fatal.
                }
            }
        }
        catch (IOException)
        {
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return result;
        }

        return result;
    }

    private static Dictionary<string, string> ReadSimpleToml(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            result[key] = value;
        }

        return result;
    }
}
