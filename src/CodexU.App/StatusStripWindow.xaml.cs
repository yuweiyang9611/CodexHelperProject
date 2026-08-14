using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Win32;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace CodexU.App;

public partial class StatusStripWindow : Window
{
    private static readonly SolidColorBrush HealthyBrush = CreateBrush(0x72, 0xD6, 0xA0);
    private static readonly SolidColorBrush NeutralBrush = CreateBrush(0x8E, 0x9A, 0xB4);
    private static readonly SolidColorBrush WarningBrush = CreateBrush(0xF0, 0xB3, 0x5A);
    private static readonly SolidColorBrush ErrorBrush = CreateBrush(0xF0, 0x78, 0x78);
    private static readonly SolidColorBrush AvailableMetricBrush = CreateBrush(0xFF, 0xFF, 0xFF);
    private static readonly SolidColorBrush UnavailableMetricBrush = CreateBrush(0x77, 0x83, 0x9C);

    private readonly Action _openDashboard;
    private readonly Func<Task<DashboardSnapshot>> _refreshData;
    private readonly Func<bool, Task> _updatePositionLock;
    private readonly StatusStripPresenter _presenter;
    private readonly StatusStripPlacementStore _placementStore;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _hoverExpandTimer;
    private Task? _activeRefreshTask;
    private bool _detailsPinned;
    private bool _pointerInside;
    private bool _suppressHoverUntilMouseLeaves;
    private bool _hoverExpanded;
    private bool _detailsVisible;
    private bool _isDragging;
    private bool _positionLocked;
    private StatusStripPixelPoint? _manualPosition;

    public StatusStripWindow(
        Action openDashboard,
        Func<Task<DashboardSnapshot>> refreshData,
        Func<bool, Task> updatePositionLock,
        AppSettings settings,
        StatusStripPlacementStore placementStore)
    {
        _openDashboard = openDashboard;
        _refreshData = refreshData;
        _updatePositionLock = updatePositionLock;
        _presenter = new StatusStripPresenter(settings);
        _positionLocked = settings.StatusStripPositionLocked;
        _placementStore = placementStore;
        _manualPosition = _placementStore.Load();
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _positionTimer.Tick += (_, _) => PositionWindow();
        _hoverExpandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _hoverExpandTimer.Tick += HoverExpandTimer_Tick;
        InitializeComponent();
        UpdatePositionLockUi();
        Render(_presenter.Current);
        Loaded += (_, _) => UpdatePositionTimer();
        IsVisibleChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                UpdatePositionTimer();
            }
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(EnsureVisiblePosition);
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        Closed += (_, _) =>
        {
            _positionTimer.Stop();
            _hoverExpandTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        };
    }

    public bool HasManualPosition => _manualPosition is not null;

    public bool PositionLocked => _positionLocked;

    public string CurrentDisplayName { get; private set; } = "尚未定位";

    public void Update(DashboardSnapshot snapshot, int? openTodos)
    {
        var previousSnapshot = _presenter.Snapshot;
        Render(_presenter.UpdateSnapshot(snapshot, openTodos));
        if (!ReferenceEquals(previousSnapshot, snapshot))
        {
            SmokeTestSignals.SignalStatusRendered();
        }
    }

    public void UpdateTodoCount(int? openTodos) =>
        Render(_presenter.UpdateTodoCount(openTodos));

    public void ApplySettings(AppSettings settings)
    {
        _positionLocked = settings.StatusStripPositionLocked;
        UpdatePositionLockUi();
        Render(_presenter.ApplySettings(settings));
    }

    public void EnsureVisiblePosition() => PositionWindow();

    public bool RecoverPosition()
    {
        if (ClearManualPosition())
        {
            PositionWindow();
            return true;
        }

        return false;
    }

    public Task RefreshAsync()
    {
        if (_activeRefreshTask is { IsCompleted: false })
        {
            return _activeRefreshTask;
        }

        _activeRefreshTask = RefreshCoreAsync();
        return _activeRefreshTask;
    }

    private async Task RefreshCoreAsync()
    {
        var generation = _presenter.BeginRefresh();
        Render(_presenter.Current);
        try
        {
            var snapshot = await _refreshData();
            var beforeCompletion = _presenter.Snapshot;
            Render(_presenter.CompleteRefresh(generation, snapshot));
            if (!ReferenceEquals(beforeCompletion, _presenter.Snapshot))
            {
                SmokeTestSignals.SignalStatusRendered();
            }
        }
        catch (OperationCanceledException)
        {
            Render(_presenter.FailRefresh(generation, "本次刷新已取消。"));
        }
        catch (Exception exception)
        {
            Render(_presenter.FailRefresh(generation, $"刷新失败：{exception.Message}"));
        }
    }

    private void Render(StatusStripPresentation presentation)
    {
        RuntimeTitle.Text = presentation.RuntimeTitle;
        PrimaryLabel.Text = presentation.PrimaryLabel;
        SecondaryLabel.Text = presentation.SecondaryLabel;
        PrimaryDetailLabel.Text = presentation.PrimaryDetailLabel;
        SecondaryDetailLabel.Text = presentation.SecondaryDetailLabel;
        TodayBlock.Visibility = presentation.ShowTodayTokens ? Visibility.Visible : Visibility.Collapsed;
        TodayColumn.Width = presentation.ShowTodayTokens
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(0d);

        PrimaryText.Text = presentation.PrimaryQuota.Text;
        SecondaryText.Text = presentation.SecondaryQuota.Text;
        TodayText.Text = presentation.Today.Text;
        WeekText.Text = presentation.SevenDays.Text;
        LifetimeText.Text = presentation.Lifetime.Text;
        TodoText.Text = presentation.TodoText;

        AutomationProperties.SetName(PrimaryBlock, presentation.PrimaryQuota.AccessibleText);
        AutomationProperties.SetName(SecondaryBlock, presentation.SecondaryQuota.AccessibleText);
        AutomationProperties.SetName(TodayBlock, presentation.Today.AccessibleText);
        AutomationProperties.SetName(WeekBlock, presentation.SevenDays.AccessibleText);
        AutomationProperties.SetName(LifetimeBlock, presentation.Lifetime.AccessibleText);
        AutomationProperties.SetName(TodoBlock, presentation.TodoAccessibleText);

        PrimaryText.Foreground = MetricBrush(presentation.PrimaryQuota.IsAvailable, degraded: false);
        SecondaryText.Foreground = MetricBrush(presentation.SecondaryQuota.IsAvailable, degraded: false);
        TodayText.Foreground = MetricBrush(presentation.Today.IsAvailable, presentation.Today.IsDegraded);
        WeekText.Foreground = MetricBrush(presentation.SevenDays.IsAvailable, presentation.SevenDays.IsDegraded);
        LifetimeText.Foreground = MetricBrush(presentation.Lifetime.IsAvailable, presentation.Lifetime.IsDegraded);
        TodoText.Foreground = presentation.TodoText == "--" ? UnavailableMetricBrush : AvailableMetricBrush;

        RenderQuotaProgress(
            PrimaryProgress,
            PrimaryProgressStatus,
            presentation.PrimaryQuota);
        RenderQuotaProgress(
            SecondaryProgress,
            SecondaryProgressStatus,
            presentation.SecondaryQuota);

        StateGlyph.Text = presentation.StateGlyph;
        ExpandToggle.Foreground = BrushFor(presentation.VisualState);
        UpdatedText.Text = presentation.StatusText;
        UpdatedText.ToolTip = presentation.StatusToolTip;
        AutomationProperties.SetName(UpdatedText, presentation.StatusText);
        AutomationProperties.SetHelpText(UpdatedText, presentation.StatusToolTip);
        RefreshButton.IsEnabled = presentation.CanRefresh;
        AutomationProperties.SetHelpText(
            RefreshButton,
            presentation.CanRefresh ? presentation.StatusToolTip : "正在刷新，请稍候。");
        UpdateExpandAutomation(presentation);
    }

    private static void RenderQuotaProgress(
        System.Windows.Controls.ProgressBar progress,
        System.Windows.Controls.TextBlock unavailableText,
        StatusStripQuotaPresentation quota)
    {
        progress.Value = quota.ProgressPercent ?? 0d;
        progress.Opacity = quota.IsAvailable ? 1d : 0.22d;
        unavailableText.Visibility = quota.IsAvailable ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(progress, quota.AccessibleText);
        AutomationProperties.SetHelpText(progress, quota.AccessibleText);
        AutomationProperties.SetItemStatus(
            progress,
            quota.IsAvailable ? quota.Text : "不可用");
    }

    private void UpdateExpandAutomation(StatusStripPresentation presentation)
    {
        var action = _detailsPinned ? "收起状态条详细信息" : "固定展开状态条详细信息";
        AutomationProperties.SetName(ExpandToggle, $"{action}；{presentation.StatusText}");
        AutomationProperties.SetHelpText(
            ExpandToggle,
            $"{presentation.StatusToolTip} 按 Enter 或空格切换固定展开，按 Escape 收起。");
        ExpandToggle.ToolTip = $"{presentation.StatusToolTip}\n{action}";
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = true;
        ArmHoverExpansion();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _pointerInside = false;
        CancelHoverExpansion();
        if (!_isDragging)
        {
            _suppressHoverUntilMouseLeaves = false;
        }

        UpdateDetailsVisibility();
    }

    private void HoverExpandTimer_Tick(object? sender, EventArgs e)
    {
        _hoverExpandTimer.Stop();
        if (!_detailsPinned
            && _pointerInside
            && !_suppressHoverUntilMouseLeaves
            && !_isDragging)
        {
            _hoverExpanded = true;
            UpdateDetailsVisibility();
        }
    }

    private void ArmHoverExpansion()
    {
        _hoverExpandTimer.Stop();
        if (!_detailsPinned
            && !_suppressHoverUntilMouseLeaves
            && !_isDragging)
        {
            _hoverExpandTimer.Start();
        }
    }

    private void CancelHoverExpansion()
    {
        _hoverExpandTimer.Stop();
        _hoverExpanded = false;
    }

    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsPinned = ExpandToggle.IsChecked == true;
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = !_detailsPinned;
        UpdateDetailsVisibility();
        UpdateExpandAutomation(_presenter.Current);
    }

    private void ExpandToggle_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space))
        {
            return;
        }

        e.Handled = true;
        _detailsPinned = !_detailsPinned;
        ExpandToggle.IsChecked = _detailsPinned;
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = !_detailsPinned;
        UpdateDetailsVisibility();
        UpdateExpandAutomation(_presenter.Current);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        e.Handled = true;
        _detailsPinned = false;
        ExpandToggle.IsChecked = false;
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = true;
        UpdateDetailsVisibility();
        UpdateExpandAutomation(_presenter.Current);
        ExpandToggle.Focus();
    }

    private void DragSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || e.ButtonState != MouseButtonState.Pressed
            || _positionLocked
            || IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = true;
        UpdateDetailsVisibility();

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var startingBounds = default(NativeRect);
        var hadStartingBounds = handle != IntPtr.Zero && GetWindowRect(handle, out startingBounds);
        _isDragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may be released before WPF begins the native move loop.
        }
        finally
        {
            _isDragging = false;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var bounds))
            {
                var moved = !hadStartingBounds
                    || bounds.Left != startingBounds.Left
                    || bounds.Top != startingBounds.Top;
                if (moved)
                {
                    CaptureAndPersistManualPosition(handle, bounds);
                }
                else
                {
                    PositionWindow();
                }
            }
        }
    }

    private void CaptureAndPersistManualPosition(IntPtr handle, NativeRect bounds)
    {
        _manualPosition = new StatusStripPixelPoint(bounds.Left, bounds.Top);
        PositionWindow();

        if (GetWindowRect(handle, out var constrainedBounds))
        {
            _manualPosition = new StatusStripPixelPoint(constrainedBounds.Left, constrainedBounds.Top);
        }

        PersistManualPosition();
    }

    private async void LockPosition_Click(object sender, RoutedEventArgs e)
    {
        var requested = LockPositionMenuItem.IsChecked;
        LockPositionMenuItem.IsEnabled = false;
        try
        {
            await _updatePositionLock(requested);
            _positionLocked = requested;
            UpdatePositionLockUi();
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Unable to update status strip position lock: {exception}");
            LockPositionMenuItem.IsChecked = _positionLocked;
            LockPositionMenuItem.ToolTip = "无法保存锁定状态，请稍后重试";
        }
        finally
        {
            LockPositionMenuItem.IsEnabled = true;
        }
    }

    private void Window_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = !_detailsPinned;
        UpdateDetailsVisibility();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        if (!ClearManualPosition())
        {
            return;
        }
        CancelHoverExpansion();
        _suppressHoverUntilMouseLeaves = !_detailsPinned;
        ResetPositionMenuItem.ToolTip = "已恢复跟随 Codex 窗口";
        PositionWindow();
    }

    private bool ClearManualPosition()
    {
        try
        {
            _placementStore.Clear();
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Trace.TraceWarning($"Unable to clear status strip placement: {exception}");
            ResetPositionMenuItem.ToolTip = "无法清除已保存的位置，请稍后重试";
            return false;
        }

        _manualPosition = null;
        return true;
    }

    private void UpdatePositionLockUi()
    {
        LockPositionMenuItem.IsChecked = _positionLocked;
        DragSurface.Cursor = _positionLocked
            ? System.Windows.Input.Cursors.Arrow
            : System.Windows.Input.Cursors.SizeAll;
        DragGlyph.FontFamily = new System.Windows.Media.FontFamily(
            _positionLocked ? "Segoe MDL2 Assets" : "Segoe UI Symbol");
        DragGlyph.Text = _positionLocked ? "\uE72E" : "⠿";
        var action = _positionLocked
            ? "位置已锁定；右键取消锁定后可拖动"
            : "按住拖动状态条；右键可锁定或重新跟随 Codex";
        DragSurface.ToolTip = action;
        AutomationProperties.SetName(DragSurface, _positionLocked ? "状态条位置已锁定" : "拖动状态条");
        AutomationProperties.SetHelpText(DragSurface, action);
        DragHandle.ToolTip = action;
        AutomationProperties.SetName(DragHandle, _positionLocked ? "状态条位置已锁定" : "拖动状态条");
        AutomationProperties.SetHelpText(DragHandle, action);
        LockPositionMenuItem.ToolTip = _positionLocked ? "取消锁定后可拖动" : "锁定后忽略鼠标拖动";
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(EnsureVisiblePosition);

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop
            or UserPreferenceCategory.General
            or UserPreferenceCategory.Window)
        {
            Dispatcher.BeginInvoke(EnsureVisiblePosition);
        }
    }

    private bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null && !ReferenceEquals(element, DragSurface))
        {
            if (element is WpfButtonBase)
            {
                return true;
            }

            element = element switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(element)
            };
        }

        return false;
    }

    private void UpdateDetailsVisibility()
    {
        var shouldShow = _detailsPinned
            || (_pointerInside
                && _hoverExpanded
                && !_suppressHoverUntilMouseLeaves
                && !_isDragging);
        if (_detailsVisible == shouldShow)
        {
            return;
        }

        _detailsVisible = shouldShow;
        Details.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        PositionWindow();
    }

    private void Open_Click(object sender, RoutedEventArgs e) => _openDashboard();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void UpdatePositionTimer()
    {
        if (IsVisible)
        {
            PositionWindow();
            _positionTimer.Start();
        }
        else
        {
            _positionTimer.Stop();
        }
    }

    private void PositionWindow()
    {
        if (_isDragging)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (_manualPosition is { } manualPosition)
        {
            var manualScreen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(
                    (int)Math.Round(manualPosition.Left),
                    (int)Math.Round(manualPosition.Top)));
            CurrentDisplayName = FriendlyDisplayName(manualScreen);
            var recoveredPosition = StatusStripLayout.RecoverRequestedPosition(
                new StatusStripPixelRect(
                    manualScreen.WorkingArea.Left,
                    manualScreen.WorkingArea.Top,
                    manualScreen.WorkingArea.Width,
                    manualScreen.WorkingArea.Height),
                GetScale(handle),
                manualPosition);
            if (Math.Abs(recoveredPosition.Left - manualPosition.Left) >= 0.5d
                || Math.Abs(recoveredPosition.Top - manualPosition.Top) >= 0.5d)
            {
                _manualPosition = recoveredPosition;
                manualPosition = recoveredPosition;
                PersistManualPosition();
            }

            ApplyLayout(
                handle,
                manualScreen.WorkingArea,
                null,
                GetScale(handle),
                manualPosition);
            return;
        }

        if (TryFindCodexWindow(out var codexWindow, out var bounds))
        {
            var screen = System.Windows.Forms.Screen.FromHandle(codexWindow);
            CurrentDisplayName = FriendlyDisplayName(screen);
            ApplyLayout(
                handle,
                screen.WorkingArea,
                new StatusStripPixelRect(
                    bounds.Left,
                    bounds.Top,
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top),
                GetScale(codexWindow));
            return;
        }

        var fallbackScreen = handle != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(handle)
            : System.Windows.Forms.Screen.PrimaryScreen;
        if (fallbackScreen is null)
        {
            return;
        }

        CurrentDisplayName = FriendlyDisplayName(fallbackScreen);
        ApplyLayout(handle, fallbackScreen.WorkingArea, null, GetScale(handle));
    }

    private void PersistManualPosition()
    {
        if (_manualPosition is not { } position)
        {
            return;
        }

        try
        {
            _placementStore.Save(position);
            ResetPositionMenuItem.ToolTip = "清除手动位置，并重新跟随 Codex 窗口";
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            Trace.TraceWarning($"Unable to persist status strip placement: {exception}");
            ResetPositionMenuItem.ToolTip = "当前位置仅在本次运行中有效；右键可恢复跟随";
        }
    }

    private void ApplyLayout(
        IntPtr handle,
        System.Drawing.Rectangle workingArea,
        StatusStripPixelRect? anchor,
        double dpiScale,
        StatusStripPixelPoint? requestedPosition = null)
    {
        var layout = StatusStripLayout.Calculate(
            new StatusStripPixelRect(
                workingArea.Left,
                workingArea.Top,
                workingArea.Width,
                workingArea.Height),
            anchor,
            dpiScale,
            _detailsVisible,
            requestedPosition);

        MaxWidth = layout.WidthDip;
        MaxHeight = layout.HeightDip;
        Width = layout.WidthDip;
        Height = layout.HeightDip;
        if (handle != IntPtr.Zero)
        {
            SetWindowPos(
                handle,
                IntPtr.Zero,
                layout.LeftPixels,
                layout.TopPixels,
                layout.WidthPixels,
                layout.HeightPixels,
                SwpNoZOrder | SwpNoActivate);
        }
    }

    private static bool TryFindCodexWindow(out IntPtr window, out NativeRect bounds)
    {
        var foundWindow = IntPtr.Zero;
        var foundBounds = default(NativeRect);
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || GetWindowTextLength(handle) == 0)
            {
                return true;
            }

            var title = new StringBuilder(GetWindowTextLength(handle) + 1);
            GetWindowText(handle, title, title.Capacity);
            var value = title.ToString();
            var hasCodexTitle = value.Contains("Codex", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase);
            if (!hasCodexTitle
                || value.Contains("codexU", StringComparison.OrdinalIgnoreCase)
                || !IsCodexProcess(handle))
            {
                return true;
            }

            if (GetWindowRect(handle, out var candidate) && candidate.Right > candidate.Left)
            {
                foundWindow = handle;
                foundBounds = candidate;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        window = foundWindow;
        bounds = foundBounds;
        return foundWindow != IntPtr.Zero;
    }

    private static bool IsCodexProcess(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.ProcessName.Contains("Codex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.Equals(process.ProcessName, "ChatGPT", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Current Microsoft Store builds of the Codex desktop app use
            // ChatGPT.exe while residing in the OpenAI.Codex package directory.
            var executable = process.MainModule?.FileName;
            return executable?.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static double GetScale(IntPtr window)
    {
        var dpi = window == IntPtr.Zero ? 0u : GetDpiForWindow(window);
        return dpi > 0u ? dpi / 96d : 1d;
    }

    private static string FriendlyDisplayName(System.Windows.Forms.Screen screen)
    {
        var device = screen.DeviceName.Replace(@"\\.\", string.Empty, StringComparison.Ordinal);
        return screen.Primary ? $"主显示器 · {device}" : device;
    }

    private static SolidColorBrush MetricBrush(bool available, bool degraded) =>
        !available ? UnavailableMetricBrush : degraded ? WarningBrush : AvailableMetricBrush;

    private static SolidColorBrush BrushFor(StatusStripVisualState state) => state switch
    {
        StatusStripVisualState.Healthy => HealthyBrush,
        StatusStripVisualState.Degraded or StatusStripVisualState.Unavailable => WarningBrush,
        StatusStripVisualState.Failed => ErrorBrush,
        _ => NeutralBrush
    };

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
}
