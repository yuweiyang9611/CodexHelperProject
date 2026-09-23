using CodexU.Application;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar.Tests;

public sealed class HistoryClearRecoveryTests
{
    [Fact]
    public async Task LockedHistory_BlocksFurtherWritesAfterFailedRollback_AndRecoversOnRestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "codexu-clear-recovery-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var todos = new TodoStore(dataDirectory);
            var originalSettings = await settingsStore.SaveAsync(new AppSettings(Theme: "dark"));
            var histories = new DailyUsageHistoryStore(dataDirectory);
            var date = new DateOnly(2026, 9, 22);
            await histories.SaveAsync(AgentRuntime.Codex, [Day(date, 123)], "all");
            await histories.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 456)], "all");
            var codexPath = histories.PathFor(AgentRuntime.Codex);
            var claudePath = histories.PathFor(AgentRuntime.ClaudeCode);
            var originalCodex = await File.ReadAllBytesAsync(codexPath);
            var originalClaude = await File.ReadAllBytesAsync(claudePath);
            var originalSettingsBytes = await File.ReadAllBytesAsync(settingsStore.SettingsPath);
            var sourcePath = Path.Combine(root, "original-session.jsonl");
            await File.WriteAllTextAsync(sourcePath, "synthetic source must survive");
            var markerPath = Path.Combine(dataDirectory, "unexpected-reader-write.txt");
            var service = new WritingDashboardService(markerPath);
            var journalPath = Path.Combine(dataDirectory, ".restore-transaction-v1.json");

            using (var session = Session(service, settingsStore, todos, originalSettings, dataDirectory))
            {
                // Checkpoint capture can read this file, but deletion and rollback
                // replacement cannot remove it while the Windows share lock is held.
                using (var locked = new FileStream(claudePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var failure = await Assert.ThrowsAsync<LocalDataRestoreRollbackException>(
                        () => session.ClearUsageHistoryAsync());
                    Assert.Contains("自动回滚失败", failure.Message, StringComparison.Ordinal);
                }

                Assert.True(File.Exists(journalPath));
                // Release the OS lock first: these calls must be rejected by the
                // failed-transaction state, not by the still-locked history file.
                foreach (var operation in new Func<Task>[]
                {
                    async () => { await session.UpdateSettingsAsync(value => value with { Theme = "light" }); },
                    async () => { await session.QueryUsageAsync(new()); },
                    async () => { await session.LoadCurrentRuntimeSnapshotAsync(); },
                    async () => { await session.LoadCombinedSnapshotsAsync(); },
                    async () => { await session.ClearUsageHistoryAsync(); }
                })
                {
                    var blocked = await Assert.ThrowsAsync<InvalidOperationException>(operation);
                    Assert.Contains("已禁止继续修改", blocked.Message, StringComparison.Ordinal);
                }
                Assert.Equal(0, service.Reads);
                Assert.False(File.Exists(markerPath));
                Assert.Equal(originalSettingsBytes, await File.ReadAllBytesAsync(settingsStore.SettingsPath));
            }

            // AppSettingsStore performs the same durable-journal recovery as app startup.
            var restartedSettingsStore = new AppSettingsStore(dataDirectory);
            var recoveredSettings = await restartedSettingsStore.LoadAsync();
            Assert.False(File.Exists(journalPath));
            Assert.Equal("dark", recoveredSettings.Theme);
            Assert.Equal(originalSettingsBytes, await File.ReadAllBytesAsync(settingsStore.SettingsPath));
            Assert.Equal(originalCodex, await File.ReadAllBytesAsync(codexPath));
            Assert.Equal(originalClaude, await File.ReadAllBytesAsync(claudePath));
            Assert.Equal("synthetic source must survive", await File.ReadAllTextAsync(sourcePath));
            Assert.Equal(123, Assert.Single(await histories.LoadAsync(AgentRuntime.Codex, "all")).Tokens.VisibleTotalTokens);
            Assert.Equal(456, Assert.Single(await histories.LoadAsync(AgentRuntime.ClaudeCode, "all")).Tokens.VisibleTotalTokens);

            using var restarted = Session(service, restartedSettingsStore, new TodoStore(dataDirectory), recoveredSettings, dataDirectory);
            await restarted.UpdateSettingsAsync(value => value with { Theme = "light" });
            await restarted.LoadCurrentRuntimeSnapshotAsync();
            Assert.Equal("light", restarted.CurrentSettings.Theme);
            Assert.Equal(1, service.Reads);
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            UsageReadContext.Invalidate(dataDirectory);
            Directory.Delete(root, recursive: true);
        }
    }

    private static DailyUsageRecord Day(DateOnly date, long tokens) =>
        new(date, new(tokens, 0, 0, 0, tokens), 0, 0, DataQuality.Detailed);

    private static ApplicationSession Session(IDashboardService service, AppSettingsStore settings, TodoStore todos,
        AppSettings current, string directory) => new(service, settings, todos, new UnusedUpdateService(), current,
            directory, new NoOpStartupRegistration(), new TestHostEnvironment(), _ => service);

    private sealed class WritingDashboardService(string markerPath) : IDashboardService
    {
        public int Reads { get; private set; }

        public async Task<DashboardSnapshot> LoadAsync(AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            await File.WriteAllTextAsync(markerPath, "readers can write retained observations", cancellationToken);
            return DashboardSnapshot.Empty(runtime);
        }
    }

    private sealed class UnusedUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(string currentVersion, bool includePrereleases, bool force,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoOpStartupRegistration : IStartupRegistration
    {
        public Task ApplyAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string AppVersion => "test";
        public string Platform => "test";
        public string Theme => "dark";
        public bool IsPackaged => false;
        public IReadOnlyList<string> Capabilities => [];
        public bool IsClosing => false;
        public bool IsGlobalHotKeyRegistered => true;
        public void SignalWebReady() { }
    }
}
