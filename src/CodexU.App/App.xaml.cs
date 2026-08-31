using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.App;

public partial class App : System.Windows.Application
{
    private string _applicationDataDirectory = string.Empty;
    private string StartupLogPath => Path.Combine(_applicationDataDirectory, "startup.log");
    private MainWindow? _mainWindow;
    private StatusStripWindow? _statusStrip;
    private DispatcherTimer? _statusStripPreviewTimer;
    private readonly StatusStripProjection _statusStripProjection = new();
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private readonly QuotaNotificationProjector _quotaNotifications = new();
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _instanceListenerCancellation;
    private AppSettingsStore? _settingsStore;
    private StatusStripPlacementStore? _statusStripPlacementStore;
    private TodoStore? _todoStore;
    private GitHubUpdateService? _updateService;
    private AppSettings _settings = new();
    private bool _isSmokeMode;
    private bool _isExiting;
    private bool _isExitPending;
    private string CloseToTrayTipMarkerPath => Path.Combine(_applicationDataDirectory, "close-to-tray-tip.seen");

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!TryResolveApplicationDataDirectory(
                out _applicationDataDirectory,
                out _isSmokeMode))
        {
            // A syntactically valid smoke request must never fall back to the user's
            // real data directory when its disposable root is missing or malformed.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LogStartup("Application startup entered");

        _singleInstanceMutex = new Mutex(true, "Local\\codexU.Windows.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                EventWaitHandle.OpenExisting("Local\\codexU.Windows.Show").Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The first instance is still starting; refusing a duplicate is sufficient.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\codexU.Windows.Show");
        StartInstanceListener();

        try
        {
            _settingsStore = new AppSettingsStore(_applicationDataDirectory);
            _statusStripPlacementStore = new StatusStripPlacementStore(_applicationDataDirectory);
            _todoStore = new TodoStore(_applicationDataDirectory);
            _updateService = new GitHubUpdateService(applicationDataDirectory: _applicationDataDirectory);
            _settings = await _settingsStore.LoadAsync();
            if (_isSmokeMode)
            {
                LogStartup("Smoke-test mode: skipped start-at-login reconciliation");
            }
            else
            {
                try
                {
                    var registeredByInstaller = StartupRegistration.IsEnabledForCurrentExecutable();
                    if (registeredByInstaller && !_settings.StartAtLogin)
                    {
                        _settings = await _settingsStore.SaveAsync(_settings with { StartAtLogin = true });
                    }
                    else if (!registeredByInstaller && _settings.StartAtLogin)
                    {
                        StartupRegistration.Apply(true);
                    }
                    else if (!registeredByInstaller && !_settings.StartAtLogin && StartupRegistration.HasRegistration())
                    {
                        StartupRegistration.Apply(false);
                    }
                }
                catch (Exception exception)
                {
                    LogStartup($"Start-at-login reconciliation failed: {exception.Message}");
                    if (_settings.StartAtLogin)
                    {
                        _settings = await _settingsStore.SaveAsync(_settings with { StartAtLogin = false });
                    }
                }
            }
            LogStartup("Settings loaded");

            _mainWindow = new MainWindow(
                DashboardService.CreateDefault(
                    _settings.CodexHome,
                    _settings.IncrementalIndexEnabled,
                    _settings.DefaultWorkspace,
                    _settings.ShowSubagents,
                    _settings.CodexExecutable,
                    _settings.CustomModelRates,
                    _settings.IsRateCatalogPinned,
                    applicationDataDirectory: _applicationDataDirectory),
                _settingsStore,
                _todoStore,
                _updateService,
                _settings,
                _applicationDataDirectory);
            _mainWindow.SnapshotChanged += OnSnapshotChanged;
            _mainWindow.SettingsChanged += OnSettingsChanged;
            _mainWindow.StatusStripStateRequested = GetStatusStripState;
            _mainWindow.StatusStripPreviewRequested = PreviewStatusStrip;
            _mainWindow.StatusStripRecoverRequested = RecoverStatusStrip;
            _mainWindow.TodosChanged += todos =>
                _statusStripProjection.PublishOpenTodoCount(todos.Count(item => !item.Done));
            _ = InitializeTodoProjectionAsync();
            _mainWindow.Closing += (_, args) =>
            {
                if (_isExiting)
                {
                    return;
                }

                args.Cancel = true;
                if (_settings.CloseToTray)
                {
                    _mainWindow.Hide();
                    ShowCloseToTrayTipOnce();
                }
                else
                {
                    ExitApplication();
                }
            };

            InitializeTray();
            _mainWindow.Show();
            ApplySettings(_settings);
            LogStartup("Main window shown");
        }
        catch (Exception exception)
        {
            LogStartup($"Startup failed: {exception}");
            System.Windows.MessageBox.Show(
                $"codexU 启动失败：{exception.Message}\n\n诊断日志：{StartupLogPath}",
                "codexU",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _isExiting = true;
            Shutdown(-1);
        }
    }

    private static bool TryResolveApplicationDataDirectory(
        out string applicationDataDirectory,
        out bool isSmokeMode)
    {
        if (SmokeTestSignals.TryGetApplicationDataDirectory(out applicationDataDirectory))
        {
            isSmokeMode = true;
            return true;
        }

        if (SmokeTestSignals.IsSmokeRequested)
        {
            applicationDataDirectory = string.Empty;
            isSmokeMode = false;
            return false;
        }

        try
        {
            applicationDataDirectory = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "codexU"));
            isSmokeMode = false;
            return Path.IsPathFullyQualified(applicationDataDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException)
        {
            applicationDataDirectory = string.Empty;
            isSmokeMode = false;
            return false;
        }
    }

    private void LogStartup(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            if (File.Exists(StartupLogPath) && new FileInfo(StartupLogPath).Length > 1024 * 1024)
            {
                File.Move(StartupLogPath, StartupLogPath + ".previous", overwrite: true);
            }
            File.AppendAllText(StartupLogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Startup logging must never prevent the application from opening.
        }
        catch (UnauthorizedAccessException)
        {
            // Startup logging must never prevent the application from opening.
        }
    }

    private void StartInstanceListener()
    {
        _instanceListenerCancellation = new CancellationTokenSource();
        var cancellation = _instanceListenerCancellation.Token;
        _ = Task.Run(() =>
        {
            try
            {
                var handles = new[] { _showEvent!, cancellation.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
            }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested)
            {
                // Normal shutdown can dispose the event as the listener exits.
            }
        }, cancellation);
    }

    private void InitializeTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 codexU", null, (_, _) => ShowMainWindow());
        menu.Items.Add("刷新", null, (_, _) => RunTrayActionAsync(async () =>
            {
                ShowMainWindow();
                if (_mainWindow is not null)
                {
                    await _mainWindow.RefreshAsync();
                }
            }));
        menu.Items.Add("紧凑模式", null, (_, _) => RunTrayActionAsync(async () =>
            {
                if (_mainWindow is not null)
                {
                    await _mainWindow.UpdateSettingsAsync(settings => settings with
                    {
                        CompactMode = !settings.CompactMode
                    });
                }
            }));
        menu.Items.Add("顶部状态条", null, (_, _) => RunTrayActionAsync(async () =>
            {
                if (_mainWindow is not null)
                {
                    await _mainWindow.UpdateSettingsAsync(settings => settings with
                    {
                        StatusStripEnabled = !settings.StatusStripEnabled
                    });
                }
            }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "codexU · 本地 AI 用量",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };
    }

    private static Icon LoadApplicationIcon()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
            if (icon is not null)
            {
                return icon;
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void ShowCloseToTrayTipOnce()
    {
        if (_trayIcon is null || File.Exists(CloseToTrayTipMarkerPath))
        {
            return;
        }

        _trayIcon.BalloonTipTitle = "codexU 仍在运行";
        _trayIcon.BalloonTipText = "主窗口已隐藏到系统托盘。单击托盘图标可重新打开，也可在设置中改为关闭时退出。";
        _trayIcon.ShowBalloonTip(5000);
        try
        {
            Directory.CreateDirectory(_applicationDataDirectory);
            File.WriteAllText(CloseToTrayTipMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogStartup($"Could not persist close-to-tray tip state: {exception.Message}");
        }
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var icon = _trayIcon.Icon;
        _trayIcon.Visible = false;
        _trayIcon.Icon = null;
        _trayIcon.Dispose();
        _trayIcon = null;
        icon?.Dispose();
    }

    private void OnSnapshotChanged(DashboardSnapshot snapshot)
    {
        try
        {
            if (ShouldShowStatusStrip(_settings) && _statusStrip is null)
            {
                ApplySettings(_settings);
            }
            _statusStripProjection.PublishSnapshot(snapshot);
        }
        catch (Exception exception)
        {
            LogStartup($"Status strip snapshot projection failed: {exception.Message}");
        }

        try
        {
            ShowQuotaNotifications(snapshot);
        }
        catch (Exception exception)
        {
            LogStartup($"Quota notification failed: {exception.Message}");
        }

    }

    private async Task InitializeTodoProjectionAsync()
    {
        try
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.InitializeTodosAsync();
            }
        }
        catch (OperationCanceledException) when (_isExiting || _isExitPending)
        {
            // Normal shutdown can cancel startup initialization.
        }
        catch (Exception exception)
        {
            _statusStripProjection.PublishOpenTodoCount(null);
            LogStartup($"Initial todo projection failed: {exception.Message}");
        }
    }

    private async void RunTrayActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            LogStartup($"Tray action failed: {exception}");
            if (_trayIcon is not null)
            {
                _trayIcon.BalloonTipTitle = "codexU 操作失败";
                _trayIcon.BalloonTipText = exception.Message;
                _trayIcon.ShowBalloonTip(5000);
            }
        }
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        _settings = settings;
        _statusStripPreviewTimer?.Stop();
        ApplySettings(settings);
        _mainWindow?.PublishStatusStripState(GetStatusStripState());
    }

    private void ApplySettings(AppSettings settings)
    {
        if (ShouldShowStatusStrip(settings))
        {
            ShowStatusStrip(settings);
        }
        else
        {
            _statusStripProjection.Detach();
            _statusStrip?.Hide();
        }
    }

    private void ShowStatusStrip(AppSettings settings)
    {
        var shouldRefresh = _statusStrip?.IsVisible != true;
        if (_statusStrip is null)
        {
            var statusStrip = new StatusStripWindow(
                ShowMainWindow,
                RefreshStatusStripDataAsync,
                async locked =>
                {
                    if (_mainWindow is not null)
                    {
                        await _mainWindow.UpdateSettingsAsync(current => current with
                        {
                            StatusStripPositionLocked = locked
                        });
                    }
                },
                settings,
                _statusStripPlacementStore
                    ?? throw new InvalidOperationException("状态条位置存储尚未初始化。"));
            statusStrip.Closed += (_, _) =>
            {
                if (ReferenceEquals(_statusStrip, statusStrip))
                {
                    _statusStripProjection.Detach();
                    _statusStrip = null;
                }
            };
            _statusStrip = statusStrip;
        }

        _statusStripProjection.Attach(_statusStrip.Update);
        _statusStrip.ApplySettings(settings);
        _statusStrip.Show();
        _statusStrip.EnsureVisiblePosition();
        if (shouldRefresh)
        {
            _ = _statusStrip.RefreshAsync();
        }
    }

    private StatusStripControlState PreviewStatusStrip(AppSettings previewSettings)
    {
        ShowStatusStrip(previewSettings);
        if (!SmokeTestSignals.IsStatusStripSmokeMode)
        {
            _statusStripPreviewTimer ??= CreateStatusStripPreviewTimer();
            _statusStripPreviewTimer.Stop();
            _statusStripPreviewTimer.Start();
        }

        return GetStatusStripState("预览已显示 12 秒；不会保存当前草稿。");
    }

    private DispatcherTimer CreateStatusStripPreviewTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplySettings(_settings);
            _mainWindow?.PublishStatusStripState(GetStatusStripState());
        };
        return timer;
    }

    private StatusStripControlState RecoverStatusStrip()
    {
        ShowStatusStrip(_settings);
        if (!_statusStrip!.RecoverPosition())
        {
            return GetStatusStripState("无法清除已保存的位置，请检查本机数据目录权限。");
        }
        if (!_settings.StatusStripEnabled && !SmokeTestSignals.IsStatusStripSmokeMode)
        {
            _statusStripPreviewTimer ??= CreateStatusStripPreviewTimer();
            _statusStripPreviewTimer.Stop();
            _statusStripPreviewTimer.Start();
        }

        return GetStatusStripState(
            _settings.StatusStripEnabled
                ? "状态条已移回当前可见工作区。"
                : "状态条已找回并临时显示 12 秒；启用后才会常驻。");
    }

    private StatusStripControlState GetStatusStripState() =>
        GetStatusStripState(_statusStrip?.IsVisible == true ? "状态条运行正常。" : "状态条当前已关闭。");

    private StatusStripControlState GetStatusStripState(string message)
    {
        var visible = _statusStrip?.IsVisible == true;
        var hasManualPosition = _statusStrip?.HasManualPosition == true;
        return new StatusStripControlState(
            _settings.StatusStripEnabled,
            visible,
            _statusStrip?.PositionLocked ?? _settings.StatusStripPositionLocked,
            hasManualPosition,
            hasManualPosition ? "手动定位" : "跟随 Codex",
            _statusStrip?.CurrentDisplayName ?? "尚未显示",
            message);
    }

    private static bool ShouldShowStatusStrip(AppSettings settings) =>
        settings.StatusStripEnabled || SmokeTestSignals.IsStatusStripSmokeMode;

    private Task<DashboardSnapshot> RefreshStatusStripDataAsync()
    {
        if (_mainWindow is null)
        {
            throw new InvalidOperationException("主数据服务尚未初始化。");
        }

        return _mainWindow.RefreshCompanionSnapshotAsync();
    }

    private void ShowQuotaNotifications(DashboardSnapshot snapshot)
    {
        if (_trayIcon is null)
        {
            return;
        }

        foreach (var notification in _quotaNotifications.Project(snapshot, _settings))
        {
            _trayIcon.BalloonTipTitle = notification.Title;
            _trayIcon.BalloonTipText = notification.Body;
            _trayIcon.ShowBalloonTip(5000);
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    private async void ExitApplication()
    {
        if (_isExiting || _isExitPending)
        {
            return;
        }

        _isExitPending = true;
        try
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.PrepareForExitAsync();
            }
        }
        catch (Exception exception)
        {
            LogStartup($"Waiting for local state to become idle before exit failed: {exception}");
        }

        _isExiting = true;
        _statusStripPreviewTimer?.Stop();
        _instanceListenerCancellation?.Cancel();
        _showEvent?.Set();
        _statusStrip?.Close();
        DisposeTrayIcon();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateService?.Dispose();
        DisposeTrayIcon();
        _instanceListenerCancellation?.Cancel();
        _showEvent?.Dispose();
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // The mutex was already released or is no longer owned by this thread.
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
