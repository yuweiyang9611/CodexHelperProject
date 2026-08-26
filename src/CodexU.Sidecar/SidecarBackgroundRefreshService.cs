using CodexU.Application;
using CodexU.Core;

namespace CodexU.Sidecar;

/// <summary>
/// Runs the sidecar's automatic refresh schedule. Snapshot projection remains an
/// ApplicationSession/IpcDispatcher responsibility; this service only announces
/// cycle start and failure.
/// </summary>
public sealed class SidecarBackgroundRefreshService
{
    private readonly ApplicationSession _session;
    private readonly IIpcEventSink _eventSink;
    private readonly Func<int, TimeSpan> _intervalFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly CancellationTokenSource _stoppingCancellation = new();
    private readonly SemaphoreSlim _scheduleChanged = new(0, 1);
    private readonly object _eventGate = new();
    private Task? _loopTask;
    private int _intervalMinutes;
    private int _started;
    private int _stopping;
    private long _scheduleVersion;

    public SidecarBackgroundRefreshService(
        ApplicationSession session,
        IIpcEventSink eventSink,
        Func<int, TimeSpan>? intervalFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(eventSink);

        _session = session;
        _eventSink = eventSink;
        _intervalFactory = intervalFactory ?? (minutes => TimeSpan.FromMinutes(minutes));
        _delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        _intervalMinutes = session.CurrentSettings.AutoRefreshMinutes;
    }

    public Task Completion => Volatile.Read(ref _loopTask) ?? Task.CompletedTask;

    public void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("The background refresh service has already started.");
        }

        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new InvalidOperationException("The background refresh service is stopping.");
        }

        _session.SettingsChanged += OnSettingsChanged;
        Volatile.Write(ref _intervalMinutes, _session.CurrentSettings.AutoRefreshMinutes);
        Volatile.Write(ref _loopTask, RunLoopAsync());
    }

    /// <summary>
    /// Prevents new refresh events and wakes an outstanding schedule delay. An
    /// already active ApplicationSession read is canceled later by CancelLifetime,
    /// after the backend has drained state mutations.
    /// </summary>
    public void StopScheduling()
    {
        lock (_eventGate)
        {
            if (Interlocked.Exchange(ref _stopping, 1) != 0)
            {
                return;
            }

            _session.SettingsChanged -= OnSettingsChanged;
        }

        _stoppingCancellation.Cancel();
        SignalScheduleChanged();
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_stoppingCancellation.IsCancellationRequested)
            {
                DrainScheduleChanges();
                var scheduleVersion = Volatile.Read(ref _scheduleVersion);
                var delay = _intervalFactory(Volatile.Read(ref _intervalMinutes));
                if (delay <= TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
                {
                    throw new InvalidOperationException(
                        "The background refresh interval must be positive or infinite.");
                }

                var elapsed = await WaitForDelayOrScheduleChangeAsync(delay);
                if (!elapsed || _stoppingCancellation.IsCancellationRequested)
                {
                    continue;
                }

                if (!TryStartRefresh(scheduleVersion))
                {
                    if (_stoppingCancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    await _session.LoadCurrentRuntimeSnapshotAsync();
                }
                catch (OperationCanceledException)
                    when (_stoppingCancellation.IsCancellationRequested || _session.IsShuttingDown)
                {
                    break;
                }
                catch (Exception exception)
                {
                    if (!TryPostEvent(
                            "usage.refreshFailed",
                            new { automatic = true, message = $"自动刷新失败：{exception.Message}" }))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stoppingCancellation.IsCancellationRequested)
        {
            // Normal stop while the schedule delay is pending.
        }
    }

    private async Task<bool> WaitForDelayOrScheduleChangeAsync(TimeSpan delay)
    {
        using var cycleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _stoppingCancellation.Token);
        var delayTask = _delayAsync(delay, cycleCancellation.Token);
        var scheduleChangedTask = _scheduleChanged.WaitAsync(cycleCancellation.Token);
        var completed = await Task.WhenAny(delayTask, scheduleChangedTask);
        cycleCancellation.Cancel();
        try
        {
            await Task.WhenAll(delayTask, scheduleChangedTask);
        }
        catch (OperationCanceledException) when (cycleCancellation.IsCancellationRequested)
        {
            // The unfinished half of the race is expected to observe cancellation.
        }

        return ReferenceEquals(completed, delayTask)
               && delayTask.IsCompletedSuccessfully;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        lock (_eventGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return;
            }

            Volatile.Write(ref _intervalMinutes, settings.AutoRefreshMinutes);
            Interlocked.Increment(ref _scheduleVersion);
        }

        SignalScheduleChanged();
    }

    private void SignalScheduleChanged()
    {
        try
        {
            _scheduleChanged.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending signal is sufficient because the current interval is
            // stored separately and always read again after the reset.
        }
    }

    private void DrainScheduleChanges()
    {
        while (_scheduleChanged.Wait(0))
        {
        }
    }

    private bool TryPostEvent(string method, object payload)
    {
        lock (_eventGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return false;
            }

            _eventSink.PostEvent(method, payload);
            return true;
        }
    }

    private bool TryStartRefresh(long scheduleVersion)
    {
        lock (_eventGate)
        {
            if (Volatile.Read(ref _stopping) != 0
                || Volatile.Read(ref _scheduleVersion) != scheduleVersion)
            {
                return false;
            }

            _eventSink.PostEvent(
                "usage.refreshStarted",
                new { runtime = _session.CurrentRuntime, automatic = true });
            return true;
        }
    }
}
