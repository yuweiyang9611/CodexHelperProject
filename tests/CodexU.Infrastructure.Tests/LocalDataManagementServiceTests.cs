using System.IO.Compression;
using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class LocalDataManagementServiceTests
{
    [Fact]
    public async Task ExportAggregatesAsync_ExcludesSensitiveAndUserAuthoredFields()
    {
        var root = CreateRoot();
        try
        {
            var service = CreateService(root);
            var secretPath = Path.Combine(root, "secret-project");
            var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex) with
            {
                Account = new AccountSnapshot("chatgpt", "pro", "secret@example.test", true),
                Projects =
                [
                    new ProjectUsage("id", "safe-name", secretPath, 123, 1, DateTimeOffset.Now, "main", 1.2, DataQuality.Detailed)
                ],
                Tasks =
                [
                    new TaskItem("task", "private task title", "safe-name", DateTimeOffset.Now, 123, TaskColumnKind.Active)
                ]
            };
            var destination = Path.Combine(root, "aggregate.json");

            await service.ExportAggregatesAsync(snapshot, destination, "json");
            var content = await File.ReadAllTextAsync(destination);

            Assert.Contains("safe-name", content, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPath, content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret@example.test", content, StringComparison.Ordinal);
            Assert.DoesNotContain("private task title", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAndRestoreAsync_RoundTripsSettingsAndTodos()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(root);
            var todoStore = new TodoStore(root);
            var service = new LocalDataManagementService(settingsStore, todoStore, root);
            var original = await settingsStore.SaveAsync(new AppSettings(Theme: "light", GlobalHotKey: "Ctrl+Alt+U"));
            _ = await todoStore.AddAsync(new TodoMutation(null, "keep me", "high", null, null));
            var backup = Path.Combine(root, "backup.json");
            await service.BackupAsync(original, backup);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            foreach (var item in await todoStore.ListAsync()) _ = await todoStore.DeleteAsync(item.Id);

            var restored = await service.RestoreAsync(backup);

            Assert.True(restored.Success);
            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal("Ctrl+Alt+U", (await settingsStore.LoadAsync()).GlobalHotKey);
            Assert.Equal("keep me", Assert.Single(await todoStore.ListAsync()).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAggregatesAsync_RefusesToOverwriteManagedDataFiles()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(root);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var service = new LocalDataManagementService(settingsStore, new TodoStore(root), root);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAggregatesAsync(
                DashboardSnapshot.Empty(AgentRuntime.Codex),
                Path.Combine(root, "settings.json"),
                "json"));

            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsOversizedTodoCollectionBeforeChangingLocalState()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(root);
            var todoStore = new TodoStore(root);
            var service = new LocalDataManagementService(settingsStore, todoStore, root);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            _ = await todoStore.AddAsync(new TodoMutation(null, "keep me", "normal", null, null));
            var createdAt = DateTimeOffset.UtcNow;
            var todos = Enumerable.Range(0, TodoStore.MaximumTodoCount + 1)
                .Select(index => new TodoItem($"id-{index}", $"todo-{index}", false, "normal", null, null, createdAt))
                .ToArray();
            var backupPath = Path.Combine(root, "oversized-backup.json");
            await File.WriteAllTextAsync(
                backupPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        exportedAt = DateTimeOffset.UtcNow,
                        settings = new AppSettings(Theme: "dark"),
                        todos
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backupPath));

            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal("keep me", Assert.Single(await todoStore.ListAsync()).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiagnosticsAndIndexRepair_AreSanitizedAndNonDestructive()
    {
        var root = CreateRoot();
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            const string configuredWorkspace = @"D:\Sensitive Workspace";
            await File.WriteAllTextAsync(
                Path.Combine(root, "startup.log"),
                $"failed at {profile}\\private\\file.json and {configuredWorkspace}\\private-project");
            var index = Path.Combine(root, "session-index-v1.json");
            await File.WriteAllTextAsync(index, "{ valid-enough-index-backup }");
            var service = CreateService(root);
            var destination = Path.Combine(root, "diagnostics.zip");

            await service.ExportDiagnosticsAsync(
                DashboardSnapshot.Empty(AgentRuntime.Codex, $"reader path {profile}\\.codex"),
                new AppSettings(DefaultWorkspace: configuredWorkspace),
                destination,
                "test");
            using (var archive = ZipFile.OpenRead(destination))
            {
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var content = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(profile))
                    {
                        Assert.DoesNotContain(profile, content, StringComparison.OrdinalIgnoreCase);
                    }
                    Assert.DoesNotContain(configuredWorkspace, content, StringComparison.OrdinalIgnoreCase);
                }
            }

            var rebuilt = await service.RebuildSessionIndexAsync();

            Assert.True(rebuilt.Success);
            Assert.False(File.Exists(index));
            Assert.NotNull(rebuilt.Path);
            Assert.True(File.Exists(rebuilt.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-local-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static LocalDataManagementService CreateService(string root) =>
        new(new AppSettingsStore(root), new TodoStore(root), root);
}
