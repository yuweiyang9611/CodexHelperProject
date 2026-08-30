using System.Text.Json;
using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar.Tests;

public sealed class IpcDispatcherTests
{
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
        private readonly string _dataDirectory;

        private TestContext(IDashboardService dashboardService)
        {
            _dataDirectory = Path.Combine(
                Path.GetTempPath(),
                $"codexu-ipc-dispatcher-{Guid.NewGuid():N}");
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

        public IpcDispatcher Dispatcher { get; }

        public RecordingEventSink EventSink { get; }

        public static TestContext Create(IDashboardService dashboardService) => new(dashboardService);

        public void Dispose()
        {
            Dispatcher.Dispose();
            Session.Dispose();
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
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
        public void Apply(bool enabled)
        {
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
}
