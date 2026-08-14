using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Web.WebView2.Core;

namespace CodexU.App;

public partial class MainWindow : Window
{
    private const double PreferredWidth = 1080;
    private const double PreferredHeight = 810;
    // The Web UI now reflows at narrow widths instead of shrinking the whole dashboard.
    // Keep the host usable on small displays and when snapped beside another window.
    private const double DesignMinimumWidth = 640;
    private const double DesignMinimumHeight = 480;
    private const double WorkAreaMargin = 16;
    private const double CompactPreferredWidth = 940;
    private const double CompactPreferredHeight = 420;
    private const double CompactMinimumWidth = 640;
    private const double CompactMinimumHeight = 380;
    private const int HotKeyId = 0xC0DE;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint VirtualKeyU = 0x55;
    private const uint VirtualKeyC = 0x43;
    private const int WmHotKey = 0x0312;
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(90);
    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private IDashboardService _dashboardService;
    private readonly AppSettingsStore _settingsStore;
    private readonly TodoStore _todoStore;
    private readonly IUpdateService _updateService;
    private readonly string _applicationDataDirectory;
    private readonly LocalDataManagementService _dataManagementService;
    private readonly RateCatalogFileService _rateCatalogFileService;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AsyncSingleFlight<AgentRuntime, DashboardSnapshot> _snapshotSingleFlight = new();
    // Settings, todos, backup and restore are one logical local state. Serializing all
    // mutations prevents a tray action from being silently overwritten mid-restore.
    private readonly SemaphoreSlim _stateMutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly LatestSelectionCoordinator<AgentRuntime> _runtimeSelection =
        new(AgentRuntime.Codex);
    private AppSettings _settings;
    private HwndSource? _windowSource;
    private bool _webReady;
    private bool _updatingWindowConstraints;
    private bool _windowConstraintUpdatePending;
    private bool _compactMode;
    private bool _hotKeyRegistered;
    private string? _registeredHotKey;
    private bool _isClosed;
    private volatile bool _stateShutdownRequested;
    private Rect? _expandedBounds;
    private UpdateCheckResult? _lastUpdateResult;
    private DashboardSnapshot? _lastSnapshot;
    private IReadOnlyList<TodoItem>? _lastTodos;

    public MainWindow(
        IDashboardService dashboardService,
        AppSettingsStore settingsStore,
        TodoStore todoStore,
        IUpdateService updateService,
        AppSettings settings,
        string applicationDataDirectory)
    {
        _dashboardService = dashboardService;
        _settingsStore = settingsStore;
        _todoStore = todoStore;
        _updateService = updateService;
        _applicationDataDirectory = Path.GetFullPath(applicationDataDirectory);
        _dataManagementService = new LocalDataManagementService(
            settingsStore,
            todoStore,
            _applicationDataDirectory);
        _rateCatalogFileService = new RateCatalogFileService(_applicationDataDirectory);
        _settings = settings;
        _compactMode = settings.CompactMode;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(settings.AutoRefreshMinutes) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        InitializeComponent();
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        DpiChanged += OnDpiChanged;
        LocationChanged += OnLocationChanged;
        Closed += OnClosed;
    }

    public event Action<DashboardSnapshot>? SnapshotChanged;

    public event Action<AppSettings>? SettingsChanged;

    public event Action<IReadOnlyList<TodoItem>>? TodosChanged;

    public Func<StatusStripControlState>? StatusStripStateRequested { get; set; }

    public Func<AppSettings, StatusStripControlState>? StatusStripPreviewRequested { get; set; }

    public Func<StatusStripControlState>? StatusStripRecoverRequested { get; set; }

    public void PublishStatusStripState(StatusStripControlState state) =>
        PostEvent("statusStrip.stateChanged", state);

    public AppSettings CurrentSettings => _settings;

    private AgentRuntime CurrentRuntime => _runtimeSelection.Current;

    public Task<DashboardSnapshot> RefreshCompanionSnapshotAsync() =>
        LoadCurrentRuntimeSnapshotAsync();

    public async Task<IReadOnlyList<TodoItem>> InitializeTodosAsync()
    {
        await _stateMutationGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ThrowIfStateMutationUnavailable();
            if (_lastTodos is not null)
            {
                return PublishTodosAfterCommit(_lastTodos);
            }

            var todos = await _todoStore.ListAsync(_lifetimeCancellation.Token);
            return PublishTodosAfterCommit(todos);
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public void SendToDesktopLayer()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    public Task<AppSettings> SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpdateSettingsAsync(_ => settings);
    }

    public async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _stateMutationGate.WaitAsync();
        try
        {
            ThrowIfStateMutationUnavailable();
            var previous = _settings;
            var normalized = update(previous).Validate().Normalize();
            var replacementService = DashboardSettingsChanged(previous, normalized)
                ? CreateDashboardService(normalized)
                : null;

            var startupRegistrationChanged = previous.StartAtLogin != normalized.StartAtLogin;
            if (startupRegistrationChanged)
            {
                StartupRegistration.Apply(normalized.StartAtLogin);
            }

            AppSettings saved;
            try
            {
                saved = await _settingsStore.SaveAsync(normalized);
            }
            catch
            {
                if (startupRegistrationChanged)
                {
                    try
                    {
                        StartupRegistration.Apply(previous.StartAtLogin);
                    }
                    catch
                    {
                        // Preserve the original settings-write failure. Startup state is
                        // checked again the next time the user saves this option.
                    }
                }

                throw;
            }
            _settings = saved;
            if (replacementService is not null)
            {
                _dashboardService = replacementService;
            }

            ProjectSettingsAfterCommit(_settings);
            return _settings;
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public async Task PrepareForExitAsync()
    {
        _stateShutdownRequested = true;
        await _stateMutationGate.WaitAsync();
        _stateMutationGate.Release();
    }

    private void ThrowIfStateMutationUnavailable()
    {
        if (_stateShutdownRequested)
        {
            throw new InvalidOperationException("应用正在退出，不能再修改本地状态。");
        }
    }

    private void ProjectSettingsAfterCommit(AppSettings settings)
    {
        var failures = new List<Exception>();
        try
        {
            ApplySettings(settings, notifyWeb: true);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (SettingsChanged is { } handlers)
        {
            foreach (var subscriber in handlers.GetInvocationList().Cast<Action<AppSettings>>())
            {
                try
                {
                    subscriber(settings);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        ReportProjectionFailures("设置", failures);
    }

    private void NotifyTodosChangedSafely(IReadOnlyList<TodoItem> todos)
    {
        var failures = new List<Exception>();
        if (TodosChanged is { } handlers)
        {
            foreach (var subscriber in handlers.GetInvocationList().Cast<Action<IReadOnlyList<TodoItem>>>())
            {
                try
                {
                    subscriber(todos);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
        }

        ReportProjectionFailures("待办", failures);
    }

    private IReadOnlyList<TodoItem> PublishTodosAfterCommit(IReadOnlyList<TodoItem> todos)
    {
        // Callers hold _stateMutationGate, so the cache and every projection observe
        // the same persisted order.
        var published = todos.ToArray();
        _lastTodos = published;
        NotifyTodosChangedSafely(published);
        return published;
    }

    private void ReportProjectionFailures(string area, IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var message = $"{area}已保存，但界面同步失败：{string.Join("；", failures.Select(item => item.Message).Distinct())}";
        try
        {
            StatusText.Text = message;
        }
        catch
        {
            // The window may already be tearing down. The persisted state remains authoritative.
        }

        try
        {
            PostEvent("app.projectionWarning", new { area, message });
        }
        catch
        {
            // Do not turn a successful persisted mutation into a failed IPC response.
        }
    }

    private static bool DashboardSettingsChanged(AppSettings previous, AppSettings current) =>
        !string.Equals(previous.CodexHome, current.CodexHome, StringComparison.OrdinalIgnoreCase)
        || previous.IncrementalIndexEnabled != current.IncrementalIndexEnabled
        || !string.Equals(previous.DefaultWorkspace, current.DefaultWorkspace, StringComparison.OrdinalIgnoreCase)
        || previous.ShowSubagents != current.ShowSubagents
        || !string.Equals(previous.CodexExecutable, current.CodexExecutable, StringComparison.OrdinalIgnoreCase)
        || !(previous.CustomModelRates ?? []).SequenceEqual(current.CustomModelRates ?? [])
        || previous.IsRateCatalogPinned != current.IsRateCatalogPinned;

    private IDashboardService CreateDashboardService(AppSettings settings) =>
        DashboardService.CreateDefault(
            settings.CodexHome,
            settings.IncrementalIndexEnabled,
            settings.DefaultWorkspace,
            settings.ShowSubagents,
            settings.CodexExecutable,
            settings.CustomModelRates,
            settings.IsRateCatalogPinned,
            applicationDataDirectory: _applicationDataDirectory);

    public async Task RefreshAsync()
    {
        try
        {
            var requestedRuntime = CurrentRuntime;
            if (_webReady)
            {
                PostEvent("usage.refreshStarted", new { runtime = requestedRuntime });
            }
            var snapshot = await LoadCurrentRuntimeSnapshotAsync();
            if (snapshot.Runtime == CurrentRuntime && _webReady)
            {
                PostEvent("usage.snapshotChanged", snapshot);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // Normal shutdown cancels an in-flight background refresh.
        }
        catch (Exception ex)
        {
            if (_webReady)
            {
                PostEvent("usage.refreshFailed", new { message = ex.Message });
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        try
        {
            StatusText.Text = "正在初始化 WebView2…";
            var webViewDataDirectory = Path.Combine(_applicationDataDirectory, "WebView2");
            Directory.CreateDirectory(webViewDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataDirectory);
            await WebView.EnsureCoreWebView2Async(environment);
            ConfigureWebView();

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!File.Exists(Path.Combine(webRoot, "index.html")))
            {
                throw new FileNotFoundException("未找到已构建的 Web UI", Path.Combine(webRoot, "index.html"));
            }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.codexu.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.DenyCors);
            WebView.Source = new Uri("https://app.codexu.local/index.html#/dashboard");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"启动失败：{ex.Message}";
        }
    }

    private async Task<DashboardSnapshot> LoadSnapshotAsync(AgentRuntime runtime)
    {
        var snapshot = await LoadSnapshotPreviewAsync(runtime);
        PublishSnapshotIfCurrent(runtime, snapshot);
        return snapshot;
    }

    private async Task<DashboardSnapshot> LoadCurrentRuntimeSnapshotAsync()
    {
        while (true)
        {
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var requestedRuntime = CurrentRuntime;
            var snapshot = await LoadSnapshotAsync(requestedRuntime);
            if (requestedRuntime == CurrentRuntime)
            {
                return snapshot;
            }
        }
    }

    private Task<DashboardSnapshot> LoadSnapshotPreviewAsync(AgentRuntime runtime) =>
        _snapshotSingleFlight.RunAsync(runtime, () => LoadSnapshotSerializedAsync(runtime));

    /// <summary>
    /// Reads both runtimes for the combined view.
    ///
    /// The gate is taken once around the pair rather than per read, so the auto-refresh
    /// timer cannot slot a third read between them and leave the two halves describing
    /// different moments. Nothing here publishes: the status strip, the tray and
    /// <c>_lastSnapshot</c> follow the selected runtime only, and opening a tab is not a
    /// reason to move them or to pop a quota balloon.
    ///
    /// It is not purely a read: each half records a quota sample, so opening this tab
    /// adds a reading for the runtime the user is not on. That is wanted — it is how the
    /// unselected runtime accumulates any forecast history at all — and re-reading an
    /// unchanged source cannot invent a burn rate, because a sample is dated by when it
    /// was measured and a repeat of the newest reading is discarded.
    /// </summary>
    private async Task<CombinedSnapshots> LoadCombinedSnapshotsAsync()
    {
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            return new CombinedSnapshots(
                await ReadRuntimeForCombinedAsync(AgentRuntime.Codex),
                await ReadRuntimeForCombinedAsync(AgentRuntime.ClaudeCode));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// One half of a combined read. A failure is reported as a failed half rather than
    /// thrown, so a Claude read that times out on a large transcript tree still leaves
    /// the user with the Codex column instead of an empty view — but it is flagged, so
    /// the missing usage is never quietly absorbed into a total that looks complete.
    /// </summary>
    private async Task<RuntimeReadResult> ReadRuntimeForCombinedAsync(AgentRuntime runtime)
    {
        try
        {
            return new RuntimeReadResult(await ReadSnapshotCoreAsync(runtime), ReadFailed: false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RuntimeReadResult(
                DashboardSnapshot.Empty(runtime, $"{runtime} 读取失败：{exception.Message}"),
                ReadFailed: true,
                exception.Message);
        }
    }

    private async Task<DashboardSnapshot> LoadSnapshotSerializedAsync(AgentRuntime runtime)
    {
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            return await ReadSnapshotCoreAsync(runtime);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<DashboardSnapshot> ReadSnapshotCoreAsync(AgentRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(SnapshotTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            _lifetimeCancellation.Token);
        DashboardSnapshot snapshot;
        while (true)
        {
            var requestedService = _dashboardService;
            try
            {
                snapshot = await requestedService.LoadAsync(runtime, cancellation.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                     && !_lifetimeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"本机数据刷新超过 {SnapshotTimeout.TotalSeconds:0} 秒，已取消本轮读取");
            }

            if (ReferenceEquals(requestedService, _dashboardService))
            {
                break;
            }

            // Settings changed the data source or rate catalog while this read was in
            // flight. Re-read through the replacement service so an obsolete snapshot
            // can never be published after the settings commit.
        }
        if (!_hotKeyRegistered)
        {
            snapshot = snapshot with
            {
                Diagnostics = snapshot.Diagnostics
                    .Append($"全局快捷键 {_settings.GlobalHotKey} 注册失败，可能已被其他应用占用")
                    .ToArray()
            };
        }

        return snapshot;
    }

    private void PublishSnapshotIfCurrent(AgentRuntime runtime, DashboardSnapshot snapshot)
    {
        if (runtime == CurrentRuntime && !ReferenceEquals(_lastSnapshot, snapshot))
        {
            _lastSnapshot = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
    }

    private void ApplySettings(AppSettings settings, bool notifyWeb)
    {
        _refreshTimer.Interval = TimeSpan.FromMinutes(settings.AutoRefreshMinutes);
        AutomationProperties.SetName(
            CloseButton,
            settings.CloseToTray ? "隐藏窗口到系统托盘" : "退出应用");
        CloseButton.ToolTip = settings.CloseToTray ? "隐藏到系统托盘" : "退出应用";
        SetCompactMode(settings.CompactMode);
        if (_windowSource is not null
            && !string.Equals(_registeredHotKey, settings.GlobalHotKey, StringComparison.OrdinalIgnoreCase))
        {
            ConfigureHotKey(settings.GlobalHotKey);
        }
        if (IsLoaded)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                WindowBackdrop.TryApply(handle, dark: !string.Equals(settings.Theme, "light", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (IsLoaded)
        {
            if (settings.DesktopMode)
            {
                SendToDesktopLayer();
            }
            else
            {
                RestoreFromDesktopLayer();
            }
        }
        if (notifyWeb && _webReady)
        {
            PostEvent("settings.changed", settings);
            PostEvent("window.compactChanged", new { enabled = _compactMode });
        }
    }

    private void SetCompactMode(bool enabled)
    {
        if (_compactMode == enabled && IsLoaded)
        {
            return;
        }

        if (enabled)
        {
            if (WindowState == WindowState.Normal && ActualWidth > 0 && ActualHeight > 0)
            {
                _expandedBounds = new Rect(Left, Top, Width, Height);
            }

            _compactMode = true;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            Width = CompactPreferredWidth;
            Height = CompactPreferredHeight;
        }
        else
        {
            _compactMode = false;
            if (_expandedBounds is { } bounds)
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = bounds.Width;
                Height = bounds.Height;
            }
            else if (IsLoaded)
            {
                Width = PreferredWidth;
                Height = PreferredHeight;
            }
        }

        if (_windowSource is not null)
        {
            ApplyWindowConstraints(GetWorkAreaInDips(new WindowInteropHelper(this).Handle));
        }
    }

    private void RestoreFromDesktopLayer()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowProcedure);
        ConfigureInitialBounds(handle);
        ConfigureHotKey(_settings.GlobalHotKey);
        WindowBackdrop.TryApply(handle, dark: true);
        if (_settings.DesktopMode)
        {
            Dispatcher.BeginInvoke(SendToDesktopLayer);
        }
    }

    private void ConfigureInitialBounds(IntPtr handle)
    {
        var workArea = GetWorkAreaInDips(handle);
        ApplyWindowConstraints(workArea);
        var initialMaximumWidth = Math.Max(MinWidth, workArea.Width - WorkAreaMargin);
        var initialMaximumHeight = Math.Max(MinHeight, workArea.Height - WorkAreaMargin);
        var preferredWidth = _compactMode ? CompactPreferredWidth : PreferredWidth;
        var preferredHeight = _compactMode ? CompactPreferredHeight : PreferredHeight;
        Width = Math.Clamp(preferredWidth, MinWidth, initialMaximumWidth);
        Height = Math.Clamp(preferredHeight, MinHeight, initialMaximumHeight);
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void ConfigureHotKey(string gesture)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (_hotKeyRegistered)
        {
            UnregisterHotKey(handle, HotKeyId);
            _hotKeyRegistered = false;
        }

        var normalized = HotKeyGesture.Normalize(gesture);
        var modifiers = ModControl;
        if (normalized.Contains("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModShift;
        if (normalized.Contains("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModAlt;
        var virtualKey = normalized.EndsWith("+C", StringComparison.OrdinalIgnoreCase)
            ? VirtualKeyC
            : VirtualKeyU;
        _hotKeyRegistered = RegisterHotKey(handle, HotKeyId, modifiers, virtualKey);
        _registeredHotKey = _hotKeyRegistered ? normalized : null;
    }

    private void OnDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            ApplyWindowConstraints(GetWorkAreaInDips(handle));
            WebView.ZoomFactor = 1d;
        });
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_updatingWindowConstraints || _windowConstraintUpdatePending || _windowSource is null)
        {
            return;
        }

        _windowConstraintUpdatePending = true;
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                ApplyWindowConstraints(GetWorkAreaInDips(handle));
            }
            finally
            {
                _windowConstraintUpdatePending = false;
            }
        });
    }

    private void ApplyWindowConstraints(Rect workArea)
    {
        _updatingWindowConstraints = true;
        try
        {
            var availableWidth = Math.Max(1, workArea.Width - WorkAreaMargin);
            var availableHeight = Math.Max(1, workArea.Height - WorkAreaMargin);
            MaxWidth = Math.Max(1, workArea.Width);
            MaxHeight = Math.Max(1, workArea.Height);
            var minimumWidth = _compactMode ? CompactMinimumWidth : DesignMinimumWidth;
            var minimumHeight = _compactMode ? CompactMinimumHeight : DesignMinimumHeight;
            MinWidth = Math.Min(minimumWidth, availableWidth);
            MinHeight = Math.Min(minimumHeight, availableHeight);
        }
        finally
        {
            _updatingWindowConstraints = false;
        }
    }

    private Rect GetWorkAreaInDips(IntPtr handle)
    {
        var pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new System.Windows.Point(pixels.Left, pixels.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(pixels.Right, pixels.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            if (IsVisible && IsActive)
            {
                SendToDesktopLayer();
            }
            else
            {
                Show();
                Activate();
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _lifetimeCancellation.Cancel();
        var handle = new WindowInteropHelper(this).Handle;
        if (_hotKeyRegistered)
        {
            UnregisterHotKey(handle, HotKeyId);
        }
        _windowSource?.RemoveHook(WindowProcedure);
        _refreshTimer.Stop();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
