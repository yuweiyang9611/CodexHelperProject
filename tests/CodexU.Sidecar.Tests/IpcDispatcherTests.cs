using System.Text.Json;
using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar.Tests;

public sealed class IpcDispatcherTests
{
    [Fact]
    public async Task StartupReconciliationRequiresTheElectronHostRoute()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());
        var request = Request(
            "settings.reconcileStartupRegistration",
            new { expected = false, actual = true });

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            context.Dispatcher.DispatchAsync(request));

        var reconciled = Assert.IsType<AppSettings>(await context.Dispatcher.DispatchAsync(
            request,
            IpcRequestRoute.ElectronHost));
        Assert.True(reconciled.StartAtLogin);
    }

    [Fact]
    public async Task InitializeReturnsHostCapabilitiesUnchanged()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());

        var initialized = Assert.IsType<InitializeResult>(
            await context.Dispatcher.DispatchAsync(Request("app.initialize")));

        Assert.Equal(
            new[] { HostCapabilityNames.NativeDialogs, HostCapabilityNames.StatusStripControl },
            initialized.Capabilities);
    }

    [Fact]
    public async Task GetSnapshotProjectsSnapshotChangedExactlyOnce()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());

        var snapshot = Assert.IsType<DashboardSnapshot>(
            await context.Dispatcher.DispatchAsync(Request("usage.getSnapshot")));

        var projected = Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
        Assert.Same(snapshot, projected.Payload);
    }

    [Fact]
    public async Task RefreshProjectsSnapshotChangedExactlyOnce()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());

        var snapshot = Assert.IsType<DashboardSnapshot>(
            await context.Dispatcher.DispatchAsync(Request("usage.refresh")));

        var projected = Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
        Assert.Same(snapshot, projected.Payload);
    }

    [Fact]
    public async Task SelectRuntimeProjectsSnapshotChangedExactlyOnce()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());

        var snapshot = Assert.IsType<DashboardSnapshot>(
            await context.Dispatcher.DispatchAsync(
                Request("runtime.select", new { runtime = AgentRuntime.ClaudeCode.ToString() })));

        Assert.Equal(AgentRuntime.ClaudeCode, snapshot.Runtime);
        var projected = Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
        Assert.Same(snapshot, projected.Payload);
    }

    [Fact]
    public async Task ConcurrentGetAndRefreshShareOneProjection()
    {
        var dashboardService = new BlockingDashboardService();
        using var context = TestContext.Create(dashboardService);

        var get = context.Dispatcher.DispatchAsync(Request("usage.getSnapshot"));
        await dashboardService.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = context.Dispatcher.DispatchAsync(Request("usage.refresh"));

        Assert.Equal(1, dashboardService.CallCount);
        dashboardService.Release();

        var snapshots = await Task.WhenAll(get, refresh);
        Assert.Same(snapshots[0], snapshots[1]);
        Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task ManyConcurrentSnapshotRequestsShareOneLoadAndProjection()
    {
        const int requestCount = 16;
        var dashboardService = new BlockingDashboardService();
        using var context = TestContext.Create(dashboardService);

        var requests = new List<Task<object?>>
        {
            context.Dispatcher.DispatchAsync(Request("usage.getSnapshot"))
        };
        await dashboardService.Started.WaitAsync(TimeSpan.FromSeconds(5));
        requests.AddRange(Enumerable.Range(1, requestCount - 1)
            .Select(index => context.Dispatcher.DispatchAsync(
                Request(index % 2 == 0 ? "usage.getSnapshot" : "usage.refresh"))));

        Assert.Equal(1, dashboardService.CallCount);
        dashboardService.Release();

        var snapshots = await Task.WhenAll(requests);
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task RebuildIndexRefreshProjectsSnapshotChangedExactlyOnce()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());

        var result = Assert.IsType<LocalOperationResult>(
            await context.Dispatcher.DispatchAsync(Request("diagnostics.rebuildIndex")));

        Assert.True(result.Success);
        Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task BackupWaitsForAnActiveRefreshBeforeReadingHistory()
    {
        var dashboardService = new BlockingDashboardService();
        using var context = TestContext.Create(dashboardService);
        var refresh = context.Session.LoadCurrentRuntimeSnapshotAsync();
        await dashboardService.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var backup = context.Session.BackupStateAsync(
            Path.Combine(context.OutputDirectory, "backup.json"));

        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                backup.WaitAsync(TimeSpan.FromMilliseconds(250)));
        }
        finally
        {
            dashboardService.Release();
        }

        await refresh;
        Assert.True((await backup).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsUpdateCompensatesAnUncertainStartupWrite(bool rollbackFails)
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-startup-compensation-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var currentSettings = await settingsStore.SaveAsync(
                new AppSettings(StartAtLogin: false));
            var startup = new UncertainStartupRegistration(rollbackFails);
            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                new TodoStore(dataDirectory),
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                startup,
                new TestHostEnvironment());

            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                session.UpdateSettingsAsync(settings => settings with { StartAtLogin = true }));

            Assert.Equal([true, false], startup.Calls);
            Assert.False(startup.Enabled);
            Assert.False(session.CurrentSettings.StartAtLogin);
            Assert.False((await settingsStore.LoadAsync()).StartAtLogin);
            if (rollbackFails)
            {
                var wrapped = Assert.IsType<InvalidOperationException>(exception);
                Assert.Equal(2, Assert.IsType<AggregateException>(wrapped.InnerException).InnerExceptions.Count);
            }
            else
            {
                Assert.Equal("startup result uncertain", exception.Message);
            }
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartupReconciliationPersistsActualStateWithoutWritingTheHostAndUsesCas()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-startup-reconcile-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var currentSettings = await settingsStore.SaveAsync(
                new AppSettings(StartAtLogin: false));
            var startup = new RecordingStartupRegistration();
            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                new TodoStore(dataDirectory),
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                startup,
                new TestHostEnvironment());

            var reconciled = await session.ReconcileStartupRegistrationAsync(
                expected: false,
                actual: true);
            var superseded = await session.ReconcileStartupRegistrationAsync(
                expected: false,
                actual: false);

            Assert.True(reconciled.StartAtLogin);
            Assert.True(superseded.StartAtLogin);
            Assert.True((await settingsStore.LoadAsync()).StartAtLogin);
            Assert.Empty(startup.Calls);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreInitializationFailureRollsBackDailyHistory()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-restore-rollback-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var todoStore = new TodoStore(dataDirectory);
            var currentSettings = (await settingsStore.SaveAsync(
                new AppSettings(Theme: "dark", StartAtLogin: false))).Normalize();
            var historyStore = new DailyUsageHistoryStore(dataDirectory);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var date = new DateOnly(2026, 8, 30);
            await historyStore.SaveAsync(
                AgentRuntime.ClaudeCode,
                [HistoryDay(date, 222)],
                scope);
            var backupPath = Path.Combine(rootDirectory, "restore.json");
            var dataManagement = new LocalDataManagementService(
                settingsStore,
                todoStore,
                dataDirectory);
            await dataManagement.BackupAsync(
                currentSettings with
                {
                    Theme = "light",
                    StartAtLogin = true,
                    CodexHome = Path.Combine(rootDirectory, "restored-codex-home")
                },
                backupPath);
            await historyStore.SaveAsync(
                AgentRuntime.ClaudeCode,
                [HistoryDay(date, 111)],
                scope);

            var startup = new RecordingStartupRegistration();
            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                todoStore,
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                startup,
                new TestHostEnvironment(),
                _ => throw new InvalidOperationException("dashboard replacement rejected"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.RestoreStateAsync(backupPath));

            Assert.False(session.CurrentSettings.StartAtLogin);
            Assert.False((await settingsStore.LoadAsync()).StartAtLogin);
            Assert.Empty(startup.Calls);
            Assert.Equal(
                111,
                Assert.Single(await historyStore.LoadAsync(AgentRuntime.ClaudeCode, scope))
                    .Tokens.TotalTokens);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreRollbackFailureBlocksFurtherStateMutationsUntilRestart()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-restore-failed-rollback-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var todoStore = new TodoStore(dataDirectory);
            var currentSettings = (await settingsStore.SaveAsync(
                new AppSettings(Theme: "dark"))).Normalize();
            var backupPath = Path.Combine(rootDirectory, "restore.json");
            var dataManagement = new LocalDataManagementService(
                settingsStore,
                todoStore,
                dataDirectory);
            await dataManagement.BackupAsync(
                currentSettings with
                {
                    Theme = "light",
                    CodexHome = Path.Combine(rootDirectory, "restored-codex-home")
                },
                backupPath);

            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                todoStore,
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                new NoOpStartupRegistration(),
                new TestHostEnvironment(),
                _ =>
                {
                    Directory.Delete(
                        Path.Combine(dataDirectory, ".restore-staging-v1"),
                        recursive: true);
                    throw new InvalidOperationException("dashboard replacement rejected");
                });

            var restoreFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.RestoreStateAsync(backupPath));
            var settingsBytesBeforeRejectedMutation = await File.ReadAllBytesAsync(
                settingsStore.SettingsPath);
            var mutationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.UpdateSettingsAsync(settings => settings with { Theme = "light" }));
            var refreshFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.LoadCurrentRuntimeSnapshotAsync());

            Assert.Contains("自动回滚也失败", restoreFailure.Message, StringComparison.Ordinal);
            Assert.Contains("已禁止继续修改", mutationFailure.Message, StringComparison.Ordinal);
            Assert.Contains("已禁止继续修改", refreshFailure.Message, StringComparison.Ordinal);
            Assert.Equal("dark", session.CurrentSettings.Theme);
            Assert.Equal(
                settingsBytesBeforeRejectedMutation,
                await File.ReadAllBytesAsync(settingsStore.SettingsPath));
            Assert.True(File.Exists(Path.Combine(dataDirectory, ".restore-transaction-v1.json")));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task BeginRestoreRollbackFailureAlsoBlocksFurtherStateMutations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-begin-restore-failed-rollback-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        var todoPath = Path.Combine(dataDirectory, "todos.json");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var todoStore = new TodoStore(dataDirectory);
            var currentSettings = (await settingsStore.SaveAsync(
                new AppSettings(Theme: "dark"))).Normalize();
            _ = await todoStore.AddAsync(new TodoMutation(
                null,
                "current todo",
                "normal",
                null,
                null));
            var backupPath = Path.Combine(rootDirectory, "restore.json");
            var dataManagement = new LocalDataManagementService(
                settingsStore,
                todoStore,
                dataDirectory);
            await dataManagement.BackupAsync(
                currentSettings with { Theme = "light" },
                backupPath);

            File.SetAttributes(todoPath, File.GetAttributes(todoPath) | FileAttributes.ReadOnly);
            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                todoStore,
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                new NoOpStartupRegistration(),
                new TestHostEnvironment());

            var restoreFailure = await Assert.ThrowsAsync<LocalDataRestoreRollbackException>(() =>
                session.RestoreStateAsync(backupPath));
            var mutationFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.UpdateSettingsAsync(settings => settings with { Theme = "system" }));
            var refreshFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.LoadCurrentRuntimeSnapshotAsync());

            Assert.Contains("检查点回滚失败", restoreFailure.Message, StringComparison.Ordinal);
            Assert.Contains("已禁止继续修改", mutationFailure.Message, StringComparison.Ordinal);
            Assert.Contains("已禁止继续修改", refreshFailure.Message, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dataDirectory, ".restore-transaction-v1.json")));
        }
        finally
        {
            if (File.Exists(todoPath))
            {
                File.SetAttributes(todoPath, FileAttributes.Normal);
            }
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Schema1RestoreDoesNotCaptureOrDependOnDailyHistory()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-schema1-history-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var settingsStore = new AppSettingsStore(dataDirectory);
            var todoStore = new TodoStore(dataDirectory);
            var currentSettings = (await settingsStore.SaveAsync(
                new AppSettings(Theme: "dark"))).Normalize();
            var historyPath = new DailyUsageHistoryStore(dataDirectory)
                .PathFor(AgentRuntime.Codex);
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var historyBytes = Enumerable.Repeat((byte)'h', 8 * 1024 * 1024 + 1).ToArray();
            await File.WriteAllBytesAsync(historyPath, historyBytes);
            var backupPath = Path.Combine(rootDirectory, "schema1.json");
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

            using var session = new ApplicationSession(
                new ImmediateDashboardService(),
                settingsStore,
                todoStore,
                new UnsupportedUpdateService(),
                currentSettings,
                dataDirectory,
                new NoOpStartupRegistration(),
                new TestHostEnvironment());

            var restored = await session.RestoreStateAsync(backupPath);

            Assert.True(restored.Success);
            Assert.Equal("light", session.CurrentSettings.Theme);
            Assert.Equal(historyBytes, await File.ReadAllBytesAsync(historyPath));
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeStopsProjectingSessionSnapshotChanges()
    {
        using var context = TestContext.Create(new ImmediateDashboardService());
        await context.Dispatcher.DispatchAsync(Request("usage.getSnapshot"));
        Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));

        context.Dispatcher.Dispose();
        await context.Session.LoadCurrentRuntimeSnapshotAsync();

        Assert.Single(context.EventSink.EventsFor("usage.snapshotChanged"));
    }

    private static IpcRequest Request(string method, object? payload = null) =>
        new(
            1,
            Guid.NewGuid().ToString("N"),
            "request",
            method,
            JsonSerializer.SerializeToElement(payload ?? new { }, IpcJson.Options));

    private sealed class TestContext : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly string _dataDirectory;

        private TestContext(IDashboardService dashboardService)
        {
            _rootDirectory = Path.Combine(
                Path.GetTempPath(),
                $"codexu-ipc-dispatcher-{Guid.NewGuid():N}");
            _dataDirectory = Path.Combine(_rootDirectory, "data");
            EventSink = new RecordingEventSink();
            var hostEnvironment = new TestHostEnvironment();
            Session = new ApplicationSession(
                dashboardService,
                new AppSettingsStore(_dataDirectory),
                new TodoStore(_dataDirectory),
                new UnsupportedUpdateService(),
                new AppSettings().Normalize(),
                _dataDirectory,
                new NoOpStartupRegistration(),
                hostEnvironment,
                _ => dashboardService);
            Dispatcher = new IpcDispatcher(
                Session,
                hostEnvironment,
                EventSink,
                new ConfirmingUserInteractionService(),
                new NoOpWindowCommands(),
                new UnsupportedExternalUriLauncher());
        }

        public ApplicationSession Session { get; }

        public string DataDirectory => _dataDirectory;

        public string OutputDirectory => _rootDirectory;

        public IpcDispatcher Dispatcher { get; }

        public RecordingEventSink EventSink { get; }

        public static TestContext Create(IDashboardService dashboardService) => new(dashboardService);

        public void Dispose()
        {
            Dispatcher.Dispose();
            Session.Dispose();
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
    }

    private sealed class ImmediateDashboardService : IDashboardService
    {
        private int _callCount;

        public Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            return Task.FromResult(DashboardSnapshot.Empty(runtime, $"call-{call}"));
        }
    }

    private sealed class BlockingDashboardService : IDashboardService
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task Started => _started.Task;

        public async Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return DashboardSnapshot.Empty(runtime, "shared");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class RecordingEventSink : IIpcEventSink
    {
        private readonly object _gate = new();
        private readonly List<RecordedEvent> _events = [];

        public void PostEvent(string method, object? payload)
        {
            lock (_gate)
            {
                _events.Add(new RecordedEvent(method, payload));
            }
        }

        public IReadOnlyList<RecordedEvent> EventsFor(string method)
        {
            lock (_gate)
            {
                return _events.Where(item => item.Method == method).ToArray();
            }
        }
    }

    private sealed record RecordedEvent(string Method, object? Payload);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string AppVersion => "1.0.0-test";

        public string Platform => "test";

        public string Theme => "dark";

        public bool IsPackaged => false;

        public IReadOnlyList<string> Capabilities =>
            [HostCapabilityNames.NativeDialogs, HostCapabilityNames.StatusStripControl];

        public bool IsClosing => false;

        public bool IsGlobalHotKeyRegistered => true;

        public void SignalWebReady()
        {
        }
    }

    private sealed class NoOpStartupRegistration : IStartupRegistration
    {
        public Task ApplyAsync(bool enabled, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingStartupRegistration : IStartupRegistration
    {
        public List<bool> Calls { get; } = [];

        public Task ApplyAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Calls.Add(enabled);
            return Task.CompletedTask;
        }
    }

    private sealed class UncertainStartupRegistration(bool rollbackFails) : IStartupRegistration
    {
        public List<bool> Calls { get; } = [];

        public bool Enabled { get; private set; }

        public Task ApplyAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Calls.Add(enabled);
            Enabled = enabled;
            return enabled || rollbackFails
                ? Task.FromException(new InvalidOperationException(
                    enabled ? "startup result uncertain" : "startup rollback failed"))
                : Task.CompletedTask;
        }
    }

    private sealed class NoOpWindowCommands : IWindowCommands
    {
        public bool IsAlwaysOnTop => false;

        public void SetAlwaysOnTop(bool enabled)
        {
        }

        public void Show()
        {
        }

        public void Activate()
        {
        }

        public void Hide()
        {
        }
    }

    private sealed class ConfirmingUserInteractionService : IUserInteractionService
    {
        public Task<string?> PickSaveFileAsync(
            HostFileDialogRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(
            HostFileDialogRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(
            HostConfirmationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class UnsupportedExternalUriLauncher : IExternalUriLauncher
    {
        public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnsupportedUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(
            string currentVersion,
            bool includePrereleases,
            bool force,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static DailyUsageRecord HistoryDay(DateOnly date, long tokens) => new(
        date,
        new TokenBreakdown(tokens, 0, 0, 0, tokens),
        CreditsUsed: 0,
        UnratedTokens: 0,
        DataQuality.Detailed);
}
