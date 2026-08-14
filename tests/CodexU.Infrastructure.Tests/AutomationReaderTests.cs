using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class AutomationReaderTests
{
    [Fact]
    public void ReadScheduledTasks_ReadsEnabledMetadataWithoutPromptBody()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-automation-{Guid.NewGuid():N}");
        var automationDirectory = Path.Combine(root, "automations", "morning");
        Directory.CreateDirectory(automationDirectory);
        File.WriteAllText(
            Path.Combine(automationDirectory, "automation.toml"),
            "name = \"Morning review\"\nenabled = true\nrrule = \"FREQ=DAILY;BYHOUR=9\"\nprompt = \"private body\"\n");
        var paths = new CodexPaths(root, root, "missing.db", "missing", "missing", Path.Combine(root, "automations"), "missing", "missing");

        try
        {
            var tasks = new AutomationReader(paths).ReadScheduledTasks();

            var task = Assert.Single(tasks);
            Assert.Equal("Morning review", task.Title);
            Assert.Equal(TaskColumnKind.Scheduled, task.Kind);
            Assert.DoesNotContain("private body", task.Detail ?? string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
