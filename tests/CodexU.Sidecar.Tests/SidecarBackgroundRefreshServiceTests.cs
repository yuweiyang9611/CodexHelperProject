using System.Text.Json;
using System.Threading.Channels;
using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarBackgroundRefreshServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FirstRefreshWaitsForCurrentConfiguredInterval()
    {
        await using var context = TestContext.Create(
            new ImmediateDashboardService(),
            autoRefreshMinutes: 7);

        context.Refresh.Start();
        var firstDelay = await context.Delay.NextAsync();

        Assert.Equal(TimeSpan.FromSeconds(7), firstDelay.Duration);
        Assert.Equal(0, context.DashboardCallCount);
        Assert.Empty(context.Events.EventsFor("usage.refreshStarted"));

        firstDelay.Release();
        _ = await context.Delay.NextAsync();

        Assert.Equal(1, context.DashboardCallCount);
        var started = Assert.Single(context.Events.EventsFor("usage.refreshStarted"));
        var startedPayload = JsonSerializer.SerializeToElement(started.Payload, IpcJson.Options);
        Assert.Equal("codex", startedPayload.GetProperty("runtime").GetString());
        Assert.True(startedPayload.GetProperty("automatic").GetBoolean());
        Assert.Single(context.Events.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task SettingsChangeImmediatelyRestartsScheduleWithNewInterval()
    {
        await using var context = TestContext.Create(
            new ImmediateDashboardService(),
            autoRefreshMinutes: 5);
        context.Refresh.Start();
        var originalDelay = await context.Delay.NextAsync();

        await context.Session.UpdateSettingsAsync(settings => settings with
        {
            AutoRefreshMinutes = 2
        });

        var resetDelay = await context.Delay.NextAsync();
        await originalDelay.CancellationObserved.WaitAsync(TestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), resetDelay.Duration);
        Assert.Equal(0, context.DashboardCallCount);

        resetDelay.Release();
        _ = await context.Delay.NextAsync();
        Assert.Equal(1, context.DashboardCallCount);
    }

    [Fact]
    public async Task FailureIsReportedAndFollowingCycleCanRecover()
    {
        await using var context = TestContext.Create(
            new FailOnceDashboardService(),
            autoRefreshMinutes: 1);
        context.Refresh.Start();
        var firstDelay = await context.Delay.NextAsync();

        firstDelay.Release();
        var secondDelay = await context.Delay.NextAsync();
        Assert.Single(context.Events.EventsFor("usage.refreshStarted"));
        Assert.Single(context.Events.EventsFor("usage.refreshFailed"));
        Assert.Empty(context.Events.EventsFor("usage.snapshotChanged"));

        secondDelay.Release();
        _ = await context.Delay.NextAsync();
        Assert.Equal(2, context.DashboardCallCount);
        Assert.Equal(2, context.Events.EventsFor("usage.refreshStarted").Count);
        Assert.Single(context.Events.EventsFor("usage.refreshFailed"));
        Assert.Single(context.Events.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task RefreshLoopNeverOverlapsCycles()
    {
        var dashboard = new ControlledDashboardService();
        await using var context = TestContext.Create(dashboard, autoRefreshMinutes: 1);
        context.Refresh.Start();
        var firstDelay = await context.Delay.NextAsync();

        firstDelay.Release();
        var firstRefresh = await dashboard.NextAsync();
        await context.Session.UpdateSettingsAsync(settings => settings with
        {
            AutoRefreshMinutes = 2
        });

        Assert.Equal(1, context.Delay.CallCount);
        Assert.Equal(1, dashboard.CallCount);
        Assert.Equal(1, dashboard.MaximumConcurrency);

        firstRefresh.Release();
        var secondDelay = await context.Delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(2), secondDelay.Duration);
        secondDelay.Release();
        var secondRefresh = await dashboard.NextAsync();

        Assert.Equal(2, dashboard.CallCount);
        Assert.Equal(1, dashboard.MaximumConcurrency);
        secondRefresh.Release();
        _ = await context.Delay.NextAsync();
    }

    [Fact]
    public async Task StopWakesTimerAndPreventsNewRefreshEvents()
    {
        await using var context = TestContext.Create(
            new ImmediateDashboardService(),
            autoRefreshMinutes: 5);
        context.Refresh.Start();
        var pendingDelay = await context.Delay.NextAsync();

        context.Refresh.StopScheduling();
        await context.Refresh.Completion.WaitAsync(TestTimeout);
        await pendingDelay.CancellationObserved.WaitAsync(TestTimeout);
        await context.Session.UpdateSettingsAsync(settings => settings with
        {
            AutoRefreshMinutes = 1
        });

        Assert.Equal(1, context.Delay.CallCount);
        Assert.Equal(0, context.DashboardCallCount);
        Assert.Empty(context.Events.EventsFor("usage.refreshStarted"));
        Assert.Empty(context.Events.EventsFor("usage.refreshFailed"));
        Assert.Empty(context.Events.EventsFor("usage.snapshotChanged"));
    }

    [Fact]
    public async Task ActiveRefreshStopsAfterSessionLifetimeCancellationWithoutFailureEvent()
    {
        var dashboard = new ControlledDashboardService();
        await using var context = TestContext.Create(dashboard, autoRefreshMinutes: 1);
        context.Refresh.Start();
        var delay = await context.Delay.NextAsync();
        delay.Release();
        var activeRefresh = await dashboard.NextAsync();

        context.Refresh.StopScheduling();
        Assert.False(context.Refresh.Completion.IsCompleted);
        context.Session.CancelLifetime();

        await context.Refresh.Completion.WaitAsync(TestTimeout);
        await activeRefresh.CancellationObserved.WaitAsync(TestTimeout);
        Assert.Single(context.Events.EventsFor("usage.refreshStarted"));
        Assert.Empty(context.Events.EventsFor("usage.refreshFailed"));
        Assert.Empty(context.Events.EventsFor("usage.snapshotChanged"));
        Assert.Equal(1, context.Delay.CallCount);
    }

    [Fact]
    public async Task ConcurrentManualAndBackgroundRefreshShareOneSnapshotProjection()
    {
        var dashboard = new ControlledDashboardService();
        await using var context = TestContext.Create(dashboard, autoRefreshMinutes: 1);
        context.Refresh.Start();
        var delay = await context.Delay.NextAsync();
        delay.Release();
        var backgroundRefresh = await dashboard.NextAsync();

        var manualRefresh = context.Dispatcher.DispatchAsync(Request("usage.refresh"));
        await Task.Yield();
        Assert.Equal(1, dashboard.CallCount);

        backgroundRefresh.Release();
        var manualSnapshot = Assert.IsType<DashboardSnapshot>(
            await manualRefresh.WaitAsync(TestTimeout));
        _ = await context.Delay.NextAsync();

        var projected = Assert.Single(context.Events.EventsFor("usage.snapshotChanged"));
        Assert.Same(manualSnapshot, projected.Payload);
        Assert.Single(context.Events.EventsFor("usage.refreshStarted"));
        Assert.Equal(1, dashboard.CallCount);
    }

    private static IpcRequest Request(string method) =>
        new(
            1,
            Guid.NewGuid().ToString("N"),
            "request",
            method,
            JsonSerializer.SerializeToElement(new { }, IpcJson.Options));

    private sealed class TestContext : IAsyncDisposable
    {
        private readonly string _dataDirectory;
        private readonly ICallCountDashboardService _dashboard;
        private bool _disposed;

        private TestContext(
            ICallCountDashboardService dashboard,
            int autoRefreshMinutes)
        {
            _dashboard = dashboard;
            _dataDirectory = Path.Combine(
                Path.GetTempPath(),
                $"codexu-background-refresh-{Guid.NewGuid():N}");
            Events = new RecordingEventSink();
            Delay = new ControlledDelay();
            var hostEnvironment = new TestHostEnvironment();
            Session = new ApplicationSession(
                dashboard,
                new AppSettingsStore(_dataDirectory),
                new TodoStore(_dataDirectory),
                new UnsupportedUpdateService(),
                new AppSettings(AutoRefreshMinutes: autoRefreshMinutes).Normalize(),
                _dataDirectory,
                new NoOpStartupRegistration(),
                hostEnvironment,
                _ => dashboard);
            Dispatcher = new IpcDispatcher(
                Session,
                hostEnvironment,
                Events,
                new CancellingUserInteractionService(),
                new NoOpWindowCommands(),
                new UnsupportedExternalUriLauncher());
            Refresh = new SidecarBackgroundRefreshService(
                Session,
                Events,
                minutes => TimeSpan.FromSeconds(minutes),
                Delay.DelayAsync);
        }

        public ApplicationSession Session { get; }

        public IpcDispatcher Dispatcher { get; }

        public RecordingEventSink Events { get; }

        public ControlledDelay Delay { get; }

        public SidecarBackgroundRefreshService Refresh { get; }

        public int DashboardCallCount => _dashboard.CallCount;

        public static TestContext Create(
            ICallCountDashboardService dashboard,
            int autoRefreshMinutes) =>
            new(dashboard, autoRefreshMinutes);

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Refresh.StopScheduling();
            Session.CancelLifetime();
            await Refresh.Completion.WaitAsync(TestTimeout);
            Dispatcher.Dispose();
            Session.Dispose();
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
    }

    private interface ICallCountDashboardService : IDashboardService
    {
        int CallCount { get; }
    }

    private sealed class ImmediateDashboardService : ICallCountDashboardService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(DashboardSnapshot.Empty(runtime, "automatic"));
        }
    }

    private sealed class FailOnceDashboardService : ICallCountDashboardService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _callCount);
            return call == 1
                ? Task.FromException<DashboardSnapshot>(new IOException("Transient read failure."))
                : Task.FromResult(DashboardSnapshot.Empty(runtime, "recovered"));
        }
    }

    private sealed class ControlledDashboardService : ICallCountDashboardService
    {
        private readonly Channel<DashboardInvocation> _invocations =
            Channel.CreateUnbounded<DashboardInvocation>();
        private int _active;
        private int _callCount;
        private int _maximumConcurrency;

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximumConcurrency(active);
            var invocation = new DashboardInvocation();
            _invocations.Writer.TryWrite(invocation);
            try
            {
                await invocation.WaitForReleaseAsync(cancellationToken);
                return DashboardSnapshot.Empty(runtime, "controlled");
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<DashboardInvocation> NextAsync() =>
            _invocations.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        private void UpdateMaximumConcurrency(int active)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _maximumConcurrency);
                if (observed >= active
                    || Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) == observed)
                {
                    return;
                }
            }
        }
    }

    private sealed class DashboardInvocation
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancellationObserved => _cancellationObserved.Task;

        public void Release() => _release.TrySetResult();

        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<DelayRequest> _requests = Channel.CreateUnbounded<DelayRequest>();
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(duration);
            Interlocked.Increment(ref _callCount);
            _requests.Writer.TryWrite(request);
            return request.WaitAsync(cancellationToken);
        }

        public Task<DelayRequest> NextAsync() =>
            _requests.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
    }

    private sealed class DelayRequest(TimeSpan duration)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan Duration { get; } = duration;

        public Task CancellationObserved => _cancellationObserved.Task;

        public void Release() => _release.TrySetResult();

        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }
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

        public IReadOnlyList<string> Capabilities => [];

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

    private sealed class CancellingUserInteractionService : IUserInteractionService
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
            Task.FromResult(false);
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
