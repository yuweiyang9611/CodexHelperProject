using CodexU.Application;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar;

public sealed class SidecarBackend : IDisposable
{
    private readonly ApplicationSession _session;
    private readonly IpcDispatcher _dispatcher;
    private readonly GitHubUpdateService _updateService;
    private readonly SidecarHostEnvironment _hostEnvironment;
    private readonly SidecarBackgroundRefreshService _backgroundRefresh;
    private readonly SidecarNativeNotificationBridge? _nativeNotifications;
    private int _stopping;
    private bool _disposed;

    private SidecarBackend(
        ApplicationSession session,
        IpcDispatcher dispatcher,
        IpcRequestProcessor requestProcessor,
        GitHubUpdateService updateService,
        SidecarHostEnvironment hostEnvironment,
        SidecarBackgroundRefreshService backgroundRefresh,
        SidecarNativeNotificationBridge? nativeNotifications)
    {
        _session = session;
        _dispatcher = dispatcher;
        RequestProcessor = requestProcessor;
        _updateService = updateService;
        _hostEnvironment = hostEnvironment;
        _backgroundRefresh = backgroundRefresh;
        _nativeNotifications = nativeNotifications;
        _session.SettingsChanged += OnSettingsChanged;
        if (_nativeNotifications is not null)
        {
            _session.SnapshotChanged += OnSnapshotChanged;
        }
    }

    public IpcRequestProcessor RequestProcessor { get; }

    public void UpdateGlobalHotKeyRegistered(bool registered) =>
        _hostEnvironment.UpdateGlobalHotKeyRegistered(registered);

    public static async Task<SidecarBackend> CreateAsync(
        SidecarOptions options,
        IIpcEventSink eventSink,
        ISidecarHostRpcClient hostRpcClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(hostRpcClient);

        Directory.CreateDirectory(options.ApplicationDataDirectory);
        var settingsStore = new AppSettingsStore(options.ApplicationDataDirectory);
        var todoStore = new TodoStore(options.ApplicationDataDirectory);
        var updateService = new GitHubUpdateService(
            applicationDataDirectory: options.ApplicationDataDirectory);

        try
        {
            var settings = (await settingsStore.LoadAsync(cancellationToken)).Normalize();
            var commands = new SidecarHostCommands(eventSink, hostRpcClient);
            var hostEnvironment = new SidecarHostEnvironment(
                options.AppVersion,
                options.Platform,
                settings.Theme,
                options.IsPackaged,
                SidecarOptions.ResolveHostCapabilities(
                    options.Platform,
                    options.IsPackaged,
                    options.NativeNotificationsAvailable),
                eventSink);
            var session = new ApplicationSession(
                CreateDashboardService(settings, options.ApplicationDataDirectory),
                settingsStore,
                todoStore,
                updateService,
                settings,
                options.ApplicationDataDirectory,
                commands,
                hostEnvironment,
                current => CreateDashboardService(current, options.ApplicationDataDirectory));
            var dispatcher = new IpcDispatcher(
                session,
                hostEnvironment,
                eventSink,
                commands,
                commands,
                commands,
                commands);
            var processor = new IpcRequestProcessor(
                dispatcher,
                hostEnvironment,
                IpcRequestRoute.ElectronHost);
            var backgroundRefresh = new SidecarBackgroundRefreshService(session, eventSink);
            var nativeNotifications = options.NativeNotificationsAvailable
                ? new SidecarNativeNotificationBridge(
                    eventSink,
                    new QuotaNotificationProjector(
                        windowIdentities: new PersistentQuotaWindowIdentityProvider(
                            options.ApplicationDataDirectory)))
                : null;
            var backend = new SidecarBackend(
                session,
                dispatcher,
                processor,
                updateService,
                hostEnvironment,
                backgroundRefresh,
                nativeNotifications);
            backgroundRefresh.Start();
            return backend;
        }
        catch
        {
            updateService.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _hostEnvironment.BeginClosing();
        _backgroundRefresh.StopScheduling();
        try
        {
            await _session.PrepareForExitAsync();
        }
        finally
        {
            _session.CancelLifetime();
            await _backgroundRefresh.Completion;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.SettingsChanged -= OnSettingsChanged;
        if (_nativeNotifications is not null)
        {
            _session.SnapshotChanged -= OnSnapshotChanged;
        }
        _backgroundRefresh.StopScheduling();
        _session.CancelLifetime();
        try
        {
            _backgroundRefresh.Completion.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // A disposal path that bypasses StopAsync still cancels the active read.
        }

        _dispatcher.Dispose();
        _session.Dispose();
        _updateService.Dispose();
    }

    private void OnSettingsChanged(AppSettings settings) =>
        _hostEnvironment.UpdateTheme(settings.Theme);

    private void OnSnapshotChanged(DashboardSnapshot snapshot) =>
        _nativeNotifications?.Publish(snapshot, _session.CurrentSettings);

    private static IDashboardService CreateDashboardService(
        AppSettings settings,
        string applicationDataDirectory) =>
        DashboardService.CreateDefault(
            settings.CodexHome,
            settings.IncrementalIndexEnabled,
            settings.DefaultWorkspace,
            settings.ShowSubagents,
            settings.CodexExecutable,
            settings.CustomModelRates,
            settings.IsRateCatalogPinned,
            applicationDataDirectory: applicationDataDirectory);
}
