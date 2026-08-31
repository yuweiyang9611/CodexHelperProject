using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task BackupAndRestoreAsync_RoundTripsSettingsTodosAndDailyHistory()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var backedUpExecutable = Path.Combine(root, "backup-codex.exe");
            var currentExecutable = Path.Combine(root, "current-codex.exe");
            var original = await settingsStore.SaveAsync(new AppSettings(
                Theme: "light",
                GlobalHotKey: "Ctrl+Alt+U",
                CodexExecutable: backedUpExecutable,
                StartAtLogin: true));
            _ = await todoStore.AddAsync(new TodoMutation(null, "keep me", "high", null, null));
            var historyStore = new DailyUsageHistoryStore(DataRoot(root));
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await historyStore.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 1), 123)],
                scope);
            await historyStore.SaveAsync(
                AgentRuntime.ClaudeCode,
                [HistoryDay(new DateOnly(2026, 8, 2), 456)],
                scope);
            var backup = Path.Combine(root, "backup.json");
            await service.BackupAsync(original, backup);
            _ = await settingsStore.SaveAsync(new AppSettings(
                Theme: "dark",
                CodexExecutable: currentExecutable));
            foreach (var item in await todoStore.ListAsync()) _ = await todoStore.DeleteAsync(item.Id);
            await historyStore.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 1), 999), HistoryDay(new DateOnly(2026, 8, 3), 777)],
                scope);

            var restored = await service.RestoreAsync(backup);

            Assert.True(restored.Success);
            Assert.Contains("用量历史", restored.Message, StringComparison.Ordinal);
            Assert.Contains("未从备份导入 Codex 可执行文件路径和本机开机启动状态", restored.Message, StringComparison.Ordinal);
            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal("Ctrl+Alt+U", (await settingsStore.LoadAsync()).GlobalHotKey);
            Assert.Equal(currentExecutable, (await settingsStore.LoadAsync()).CodexExecutable);
            Assert.False((await settingsStore.LoadAsync()).StartAtLogin);
            Assert.Equal("keep me", Assert.Single(await todoStore.ListAsync()).Text);
            Assert.Equal(123, Assert.Single(await historyStore.LoadAsync(AgentRuntime.Codex, scope)).Tokens.TotalTokens);
            Assert.Equal(456, Assert.Single(await historyStore.LoadAsync(AgentRuntime.ClaudeCode, scope)).Tokens.TotalTokens);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(backup));
            Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
            var manifest = document.RootElement.GetProperty("manifest");
            Assert.Equal("SHA-256", manifest.GetProperty("hashAlgorithm").GetString());
            var files = manifest.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(4, files.Length);
            Assert.Contains(files, entry => entry.GetProperty("path").GetString() == "settings.json");
            Assert.Contains(files, entry => entry.GetProperty("path").GetString() == "todos.json");
            Assert.Contains(files, entry => entry.GetProperty("path").GetString() == "history/daily-usage-codex-v1.jsonl");
            Assert.Contains(files, entry => entry.GetProperty("path").GetString() == "history/daily-usage-claude-code-v1.jsonl");
            foreach (var entry in files)
            {
                var content = Convert.FromBase64String(entry.GetProperty("contentBase64").GetString()!);
                Assert.Equal(content.LongLength, entry.GetProperty("size").GetInt64());
                Assert.Equal(
                    Convert.ToHexStringLower(SHA256.HashData(content)),
                    entry.GetProperty("sha256").GetString());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_PreservesEnabledHostStartupWhenBackupDisabledIt()
    {
        var root = CreateRoot();
        try
        {
            var dataRoot = DataRoot(root);
            var settingsStore = new AppSettingsStore(dataRoot);
            var todoStore = new TodoStore(dataRoot);
            var service = new LocalDataManagementService(settingsStore, todoStore, dataRoot);
            var backupPath = Path.Combine(root, "startup-disabled-backup.json");
            var currentExecutable = Path.Combine(root, "current-codex.exe");

            var backupSettings = await settingsStore.SaveAsync(new AppSettings(
                Theme: "light",
                CodexExecutable: Path.Combine(root, "backup-codex.exe"),
                StartAtLogin: false));
            await service.BackupAsync(backupSettings, backupPath);
            _ = await settingsStore.SaveAsync(new AppSettings(
                Theme: "dark",
                CodexExecutable: currentExecutable,
                StartAtLogin: true));

            var restored = await service.RestoreAsync(backupPath);
            var settings = await settingsStore.LoadAsync();

            Assert.True(restored.Success);
            Assert.Equal("light", settings.Theme);
            Assert.Equal(currentExecutable, settings.CodexExecutable);
            Assert.True(settings.StartAtLogin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_Schema2WithoutHistoryRemovesHistoryCreatedAfterBackup()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var settings = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var backup = Path.Combine(root, "backup.json");
            await service.BackupAsync(settings, backup);

            var historyStore = new DailyUsageHistoryStore(DataRoot(root));
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await historyStore.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 4), 100)],
                scope);

            await service.RestoreAsync(backup);

            Assert.Empty(await historyStore.LoadAsync(AgentRuntime.Codex, scope));
            Assert.False(File.Exists(historyStore.PathFor(AgentRuntime.Codex)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BeginRestoreAsync_UncommittedTransactionIsRecoveredOnNextStartup()
    {
        var root = CreateRoot();
        LocalDataRestoreTransaction? transaction = null;
        try
        {
            var sourceData = Path.Combine(root, "source-data");
            var sourceSettingsStore = new AppSettingsStore(sourceData);
            var sourceTodoStore = new TodoStore(sourceData);
            var sourceService = new LocalDataManagementService(
                sourceSettingsStore,
                sourceTodoStore,
                sourceData);
            var sourceSettings = await sourceSettingsStore.SaveAsync(new AppSettings(Theme: "light"));
            _ = await sourceTodoStore.AddAsync(
                new TodoMutation(null, "from backup", "high", null, null));
            var sourceHistory = new DailyUsageHistoryStore(sourceData);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await sourceHistory.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 20), 900)],
                scope);
            await sourceHistory.SaveAsync(
                AgentRuntime.ClaudeCode,
                [HistoryDay(new DateOnly(2026, 8, 21), 901)],
                scope);
            var backupPath = Path.Combine(root, "crash-recovery-backup.json");
            await sourceService.BackupAsync(sourceSettings, backupPath);

            var destinationData = Path.Combine(root, "destination-data");
            var destinationSettingsStore = new AppSettingsStore(destinationData);
            var destinationTodoStore = new TodoStore(destinationData);
            var currentExecutable = Path.Combine(root, "current-codex.exe");
            _ = await destinationSettingsStore.SaveAsync(new AppSettings(
                Theme: "dark",
                CodexExecutable: currentExecutable));
            _ = await destinationTodoStore.AddAsync(
                new TodoMutation(null, "current todo", "normal", null, null));
            var destinationHistory = new DailyUsageHistoryStore(destinationData);
            await destinationHistory.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 20), 100)],
                scope);
            await destinationHistory.SaveAsync(
                AgentRuntime.ClaudeCode,
                [HistoryDay(new DateOnly(2026, 8, 21), 101)],
                scope);
            var destinationService = new LocalDataManagementService(
                destinationSettingsStore,
                destinationTodoStore,
                destinationData);

            transaction = await destinationService.BeginRestoreAsync(backupPath);
            Assert.Equal("light", (await destinationSettingsStore.LoadAsync()).Theme);
            Assert.Equal("from backup", Assert.Single(await destinationTodoStore.ListAsync()).Text);
            Assert.Equal(
                900,
                Assert.Single(await destinationHistory.LoadAsync(AgentRuntime.Codex, scope))
                    .Tokens.TotalTokens);

            // Constructing the first persistent store is equivalent to the next
            // process startup. It must finish the pending rollback before loading.
            _ = new AppSettingsStore(destinationData);

            Assert.Equal("dark", (await destinationSettingsStore.LoadAsync()).Theme);
            Assert.Equal(currentExecutable, (await destinationSettingsStore.LoadAsync()).CodexExecutable);
            Assert.Equal("current todo", Assert.Single(await destinationTodoStore.ListAsync()).Text);
            Assert.Equal(
                100,
                Assert.Single(await destinationHistory.LoadAsync(AgentRuntime.Codex, scope))
                    .Tokens.TotalTokens);
            Assert.Equal(
                101,
                Assert.Single(await destinationHistory.LoadAsync(AgentRuntime.ClaudeCode, scope))
                    .Tokens.TotalTokens);
            Assert.False(File.Exists(Path.Combine(destinationData, LocalRestoreJournal.JournalFileName)));
            Assert.False(Directory.Exists(Path.Combine(destinationData, LocalRestoreJournal.StagingDirectoryName)));
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupRecovery_ValidatesEveryCheckpointBeforeMutatingLiveFiles()
    {
        var root = CreateRoot();
        LocalDataRestoreTransaction? transaction = null;
        try
        {
            var sourceData = Path.Combine(root, "source-data");
            var sourceSettingsStore = new AppSettingsStore(sourceData);
            var sourceTodoStore = new TodoStore(sourceData);
            var sourceSettings = await sourceSettingsStore.SaveAsync(new AppSettings(Theme: "light"));
            _ = await sourceTodoStore.AddAsync(
                new TodoMutation(null, "from backup", "normal", null, null));
            var backupPath = Path.Combine(root, "checkpoint-validation-backup.json");
            await new LocalDataManagementService(sourceSettingsStore, sourceTodoStore, sourceData)
                .BackupAsync(sourceSettings, backupPath);

            var destinationData = Path.Combine(root, "destination-data");
            var settingsStore = new AppSettingsStore(destinationData);
            var todoStore = new TodoStore(destinationData);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            _ = await todoStore.AddAsync(
                new TodoMutation(null, "current todo", "normal", null, null));
            var service = new LocalDataManagementService(settingsStore, todoStore, destinationData);
            transaction = await service.BeginRestoreAsync(backupPath);
            var restoredSettingsBytes = await File.ReadAllBytesAsync(
                Path.Combine(destinationData, "settings.json"));
            var restoredTodoBytes = await File.ReadAllBytesAsync(
                Path.Combine(destinationData, "todos.json"));
            var lastCheckpointPath = Path.Combine(
                destinationData,
                LocalRestoreJournal.StagingDirectoryName,
                "03.bin");
            var checkpointBytes = await File.ReadAllBytesAsync(lastCheckpointPath);
            var tamperedBytes = checkpointBytes.ToArray();
            tamperedBytes[0] ^= 0xff;
            await File.WriteAllBytesAsync(lastCheckpointPath, tamperedBytes);

            Assert.Throws<InvalidDataException>(() => new AppSettingsStore(destinationData));

            Assert.Equal(
                restoredSettingsBytes,
                await File.ReadAllBytesAsync(Path.Combine(destinationData, "settings.json")));
            Assert.Equal(
                restoredTodoBytes,
                await File.ReadAllBytesAsync(Path.Combine(destinationData, "todos.json")));
            Assert.True(File.Exists(Path.Combine(destinationData, LocalRestoreJournal.JournalFileName)));

            await File.WriteAllBytesAsync(lastCheckpointPath, checkpointBytes);
            await transaction.RollbackAsync();
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackAsync_RestoresPrimaryAndBackupGenerationsByteForByte()
    {
        var root = CreateRoot();
        try
        {
            var sourceData = Path.Combine(root, "source-data");
            var sourceSettingsStore = new AppSettingsStore(sourceData);
            var sourceTodoStore = new TodoStore(sourceData);
            var sourceSettings = await sourceSettingsStore.SaveAsync(new AppSettings(Theme: "light"));
            _ = await sourceTodoStore.AddAsync(
                new TodoMutation(null, "from backup", "normal", null, null));
            var backupPath = Path.Combine(root, "exact-rollback-backup.json");
            await new LocalDataManagementService(sourceSettingsStore, sourceTodoStore, sourceData)
                .BackupAsync(sourceSettings, backupPath);

            var destinationData = Path.Combine(root, "destination-data");
            var settingsStore = new AppSettingsStore(destinationData);
            var todoStore = new TodoStore(destinationData);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "system"));
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            _ = await todoStore.AddAsync(
                new TodoMutation(null, "first generation", "normal", null, null));
            _ = await todoStore.AddAsync(
                new TodoMutation(null, "second generation", "normal", null, null));
            var managedFiles = new[]
            {
                "settings.json",
                "settings.json.bak",
                "todos.json",
                "todos.json.bak"
            };
            var before = managedFiles.ToDictionary(
                relativePath => relativePath,
                relativePath => File.ReadAllBytes(Path.Combine(destinationData, relativePath)),
                StringComparer.Ordinal);
            var service = new LocalDataManagementService(settingsStore, todoStore, destinationData);

            await using var transaction = await service.BeginRestoreAsync(backupPath);
            await transaction.RollbackAsync();

            foreach (var relativePath in managedFiles)
            {
                Assert.Equal(before[relativePath], await File.ReadAllBytesAsync(
                    Path.Combine(destinationData, relativePath)));
            }
            Assert.False(File.Exists(Path.Combine(destinationData, LocalRestoreJournal.JournalFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_RejectsASnapshotThatItsRestoreLimitsCannotAccept()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var settings = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var historyStore = new DailyUsageHistoryStore(DataRoot(root));
            Directory.CreateDirectory(Path.GetDirectoryName(historyStore.PathFor(AgentRuntime.Codex))!);
            var maximumHistory = new string('x', 8 * 1024 * 1024);
            await File.WriteAllTextAsync(historyStore.PathFor(AgentRuntime.Codex), maximumHistory);
            await File.WriteAllTextAsync(historyStore.PathFor(AgentRuntime.ClaudeCode), maximumHistory);
            var backupPath = Path.Combine(root, "oversized-payload-backup.json");
            await File.WriteAllTextAsync(backupPath, "preserve existing backup");

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.BackupAsync(settings, backupPath));

            Assert.Contains("总量", exception.Message, StringComparison.Ordinal);
            Assert.Equal("preserve existing backup", await File.ReadAllTextAsync(backupPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_AcceptsSchema1AndLeavesCurrentHistoryUntouched()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var currentExecutable = Path.Combine(root, "current-codex.exe");
            _ = await settingsStore.SaveAsync(new AppSettings(
                Theme: "dark",
                CodexExecutable: currentExecutable));
            var historyStore = new DailyUsageHistoryStore(DataRoot(root));
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await historyStore.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 5), 321)],
                scope);
            var backupPath = Path.Combine(root, "legacy-backup.json");
            await File.WriteAllTextAsync(
                backupPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        exportedAt = DateTimeOffset.UtcNow,
                        settings = new AppSettings(
                            Theme: "light",
                            CodexExecutable: Path.Combine(root, "legacy-backup-codex.exe"),
                            StartAtLogin: true),
                        todos = new[]
                        {
                            new TodoItem("legacy", "from schema 1", false, "normal", null, null, DateTimeOffset.UtcNow)
                        }
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var restored = await service.RestoreAsync(backupPath);

            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal(currentExecutable, (await settingsStore.LoadAsync()).CodexExecutable);
            Assert.False((await settingsStore.LoadAsync()).StartAtLogin);
            Assert.Equal("from schema 1", Assert.Single(await todoStore.ListAsync()).Text);
            Assert.Equal(321, Assert.Single(await historyStore.LoadAsync(AgentRuntime.Codex, scope)).Tokens.TotalTokens);
            Assert.Contains("旧版备份", restored.Message, StringComparison.Ordinal);
            Assert.Contains("未从备份导入 Codex 可执行文件路径和本机开机启动状态", restored.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_Schema1DoesNotReadOversizedCurrentHistory()
    {
        var root = CreateRoot();
        try
        {
            var dataRoot = DataRoot(root);
            var settingsStore = new AppSettingsStore(dataRoot);
            var todoStore = new TodoStore(dataRoot);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            var historyPath = new DailyUsageHistoryStore(dataRoot).PathFor(AgentRuntime.Codex);
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var historyBytes = Enumerable.Repeat((byte)'x', 8 * 1024 * 1024 + 1).ToArray();
            await File.WriteAllBytesAsync(historyPath, historyBytes);
            var expectedHash = SHA256.HashData(historyBytes);
            var backupPath = Path.Combine(root, "legacy-with-large-current-history.json");
            await File.WriteAllTextAsync(
                backupPath,
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        exportedAt = DateTimeOffset.UtcNow,
                        settings = new AppSettings(Theme: "light"),
                        todos = Array.Empty<TodoItem>()
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var service = new LocalDataManagementService(settingsStore, todoStore, dataRoot);

            var restored = await service.RestoreAsync(backupPath);

            Assert.True(restored.Success);
            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal(expectedHash, SHA256.HashData(await File.ReadAllBytesAsync(historyPath)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsBackupLargerThanLimitBeforeChangingState()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            var backupPath = Path.Combine(root, "too-large-backup.json");
            await using (var stream = new FileStream(
                backupPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                stream.SetLength(24L * 1024 * 1024 + 1);
            }
            var service = new LocalDataManagementService(
                settingsStore,
                todoStore,
                DataRoot(root));

            await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backupPath));

            Assert.Equal("dark", (await settingsStore.LoadAsync()).Theme);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsTamperedManifestBeforeChangingLocalState()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var backedUp = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var backupPath = Path.Combine(root, "tampered-backup.json");
            await service.BackupAsync(backedUp, backupPath);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            _ = await todoStore.AddAsync(new TodoMutation(null, "keep current", "normal", null, null));

            var rootNode = JsonNode.Parse(await File.ReadAllTextAsync(backupPath))!.AsObject();
            var settingsEntry = rootNode["manifest"]!["files"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["path"]!.GetValue<string>() == "settings.json");
            var tampered = Encoding.UTF8.GetBytes("{}");
            settingsEntry["size"] = tampered.Length;
            settingsEntry["contentBase64"] = Convert.ToBase64String(tampered);
            await File.WriteAllTextAsync(backupPath, rootNode.ToJsonString());

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backupPath));

            Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
            Assert.Equal("dark", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal("keep current", Assert.Single(await todoStore.ListAsync()).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsUnknownManifestPathBeforeChangingLocalState()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
            var backedUp = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var historyStore = new DailyUsageHistoryStore(DataRoot(root));
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await historyStore.SaveAsync(
                AgentRuntime.Codex,
                [HistoryDay(new DateOnly(2026, 8, 6), 100)],
                scope);
            var backupPath = Path.Combine(root, "unsafe-backup.json");
            await service.BackupAsync(backedUp, backupPath);
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));

            var rootNode = JsonNode.Parse(await File.ReadAllTextAsync(backupPath))!.AsObject();
            var historyEntry = rootNode["manifest"]!["files"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["path"]!.GetValue<string>().StartsWith("history/", StringComparison.Ordinal));
            historyEntry["path"] = "../settings.json";
            await File.WriteAllTextAsync(backupPath, rootNode.ToJsonString());

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(backupPath));

            Assert.Contains("路径", exception.Message, StringComparison.Ordinal);
            Assert.Equal("dark", (await settingsStore.LoadAsync()).Theme);
            Assert.Equal(100, Assert.Single(await historyStore.LoadAsync(AgentRuntime.Codex, scope)).Tokens.TotalTokens);
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
            var settingsStore = new AppSettingsStore(DataRoot(root));
            _ = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var service = new LocalDataManagementService(
                settingsStore,
                new TodoStore(DataRoot(root)),
                DataRoot(root));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAggregatesAsync(
                DashboardSnapshot.Empty(AgentRuntime.Codex),
                Path.Combine(DataRoot(root), "settings.json"),
                "json"));

            Assert.Equal("light", (await settingsStore.LoadAsync()).Theme);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_RefusesEveryDestinationInsideApplicationData()
    {
        var root = CreateRoot();
        try
        {
            var dataRoot = DataRoot(root);
            var settingsStore = new AppSettingsStore(dataRoot);
            var settings = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var service = new LocalDataManagementService(
                settingsStore,
                new TodoStore(dataRoot),
                dataRoot);
            var target = Path.Combine(dataRoot, "quota-samples-v1.json");
            await File.WriteAllTextAsync(target, "preserve managed data");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.BackupAsync(settings, target));

            Assert.Equal("preserve managed data", await File.ReadAllTextAsync(target));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BackupAsync_RefusesDestinationThroughDirectoryLinkToApplicationData()
    {
        var root = CreateRoot();
        var alias = Path.Combine(root, "data-alias");
        try
        {
            var dataRoot = DataRoot(root);
            var settingsStore = new AppSettingsStore(dataRoot);
            var settings = await settingsStore.SaveAsync(new AppSettings(Theme: "light"));
            var service = new LocalDataManagementService(
                settingsStore,
                new TodoStore(dataRoot),
                dataRoot);
            try
            {
                Directory.CreateSymbolicLink(alias, dataRoot);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Some Windows test hosts do not permit creating symbolic links.
                return;
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.BackupAsync(settings, Path.Combine(alias, "linked-backup.json")));

            Assert.False(File.Exists(Path.Combine(dataRoot, "linked-backup.json")));
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsOversizedTodoCollectionBeforeChangingLocalState()
    {
        var root = CreateRoot();
        try
        {
            var settingsStore = new AppSettingsStore(DataRoot(root));
            var todoStore = new TodoStore(DataRoot(root));
            var service = new LocalDataManagementService(settingsStore, todoStore, DataRoot(root));
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
            Directory.CreateDirectory(DataRoot(root));
            await File.WriteAllTextAsync(
                Path.Combine(DataRoot(root), "startup.log"),
                $"failed at {profile}\\private\\file.json and {configuredWorkspace}\\private-project "
                + "C:/Sensitive/forward-slash.json file:///C:/Sensitive/uri.json "
                + "/home/alice/private.json file:///home/alice/uri.json");
            var electronLogs = Path.Combine(root, "electron-logs");
            Directory.CreateDirectory(electronLogs);
            await File.WriteAllTextAsync(
                Path.Combine(electronLogs, "codexu.log"),
                $"renderer failed at {profile}\\private\\renderer.json token=secret-value "
                + "C:/Users/alice/renderer.json /tmp/codexu/private.log");
            var index = Path.Combine(DataRoot(root), "session-index-v1.json");
            await File.WriteAllTextAsync(index, "{ valid-enough-index-backup }");
            var service = new LocalDataManagementService(
                new AppSettingsStore(DataRoot(root)),
                new TodoStore(DataRoot(root)),
                DataRoot(root),
                electronLogs);
            var destination = Path.Combine(root, "diagnostics.zip");

            await service.ExportDiagnosticsAsync(
                DashboardSnapshot.Empty(AgentRuntime.Codex, $"reader path {profile}\\.codex"),
                new AppSettings(DefaultWorkspace: configuredWorkspace),
                destination,
                "test");
            using (var archive = ZipFile.OpenRead(destination))
            {
                Assert.Contains(archive.Entries, entry => entry.FullName == "electron/codexu.log");
                var pathMarkerFound = false;
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    var content = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(profile))
                    {
                        Assert.DoesNotContain(profile, content, StringComparison.OrdinalIgnoreCase);
                    }
                    Assert.DoesNotContain(configuredWorkspace, content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("secret-value", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("C:/Sensitive/forward-slash.json", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("file:///C:/Sensitive/uri.json", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("C:/Users/alice/renderer.json", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("/home/alice/private.json", content, StringComparison.Ordinal);
                    Assert.DoesNotContain("file:///home/alice/uri.json", content, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain("/tmp/codexu/private.log", content, StringComparison.Ordinal);
                    pathMarkerFound |= content.Contains("%LOCAL_PATH", StringComparison.Ordinal);
                }
                Assert.True(pathMarkerFound);
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
        new(
            new AppSettingsStore(DataRoot(root)),
            new TodoStore(DataRoot(root)),
            DataRoot(root));

    private static string DataRoot(string root) => Path.Combine(root, "data");

    private static DailyUsageRecord HistoryDay(DateOnly date, long tokens) => new(
        date,
        new TokenBreakdown(tokens, 0, 0, 0, tokens),
        CreditsUsed: 0,
        UnratedTokens: 0,
        DataQuality.Detailed);
}
