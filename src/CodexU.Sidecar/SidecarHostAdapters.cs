using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;

namespace CodexU.Sidecar;

public sealed class SidecarEventSink(LengthPrefixedJsonTransport transport) : IIpcEventSink
{
    private readonly LengthPrefixedJsonTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));

    public void PostEvent(string method, object? payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        _transport.WriteFrameAsync(
                IpcEvent.Create(method, payload),
                IpcJson.Options,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }
}

public sealed class SidecarHostEnvironment : IHostEnvironment
{
    private readonly IIpcEventSink _eventSink;
    private string _theme;
    private int _closing;
    private int _globalHotKeyRegistered = 1;

    public SidecarHostEnvironment(
        string appVersion,
        string platform,
        string theme,
        bool isPackaged,
        IReadOnlyList<string> capabilities,
        IIpcEventSink eventSink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(eventSink);

        AppVersion = appVersion;
        Platform = platform;
        _theme = theme;
        IsPackaged = isPackaged;
        Capabilities = capabilities;
        _eventSink = eventSink;
    }

    public string AppVersion { get; }

    public string Platform { get; }

    public string Theme => Volatile.Read(ref _theme);

    public bool IsPackaged { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public bool IsClosing => Volatile.Read(ref _closing) != 0;

    // Optimistically true until Electron reports the registration result. This
    // avoids presenting a false failure diagnostic during startup ordering.
    public bool IsGlobalHotKeyRegistered => Volatile.Read(ref _globalHotKeyRegistered) != 0;

    public void SignalWebReady() =>
        _eventSink.PostEvent("host.webReady", new { ready = true });

    public void UpdateTheme(string theme) => Volatile.Write(ref _theme, theme);

    public void UpdateGlobalHotKeyRegistered(bool registered) =>
        Interlocked.Exchange(ref _globalHotKeyRegistered, registered ? 1 : 0);

    public void BeginClosing() => Interlocked.Exchange(ref _closing, 1);
}

/// <summary>
/// Bridges application host ports to Electron-facing events and correlated
/// reverse-RPC requests.
/// </summary>
public sealed class SidecarHostCommands(
    IIpcEventSink eventSink,
    ISidecarHostRpcClient hostRpcClient) :
    IStartupRegistration,
    IWindowCommands,
    IExternalUriLauncher,
    IUserInteractionService,
    IStatusStripCommands
{
    private readonly IIpcEventSink _eventSink =
        eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    private readonly ISidecarHostRpcClient _hostRpcClient =
        hostRpcClient ?? throw new ArgumentNullException(nameof(hostRpcClient));
    private int _alwaysOnTop;

    public bool IsAlwaysOnTop => Volatile.Read(ref _alwaysOnTop) != 0;

    public void Apply(bool enabled) =>
        _eventSink.PostEvent("host.startupRegistrationRequested", new { enabled });

    public void SetAlwaysOnTop(bool enabled)
    {
        Interlocked.Exchange(ref _alwaysOnTop, enabled ? 1 : 0);
        _eventSink.PostEvent("host.window.setAlwaysOnTop", new { enabled });
    }

    public void Show() => _eventSink.PostEvent("host.window.show", null);

    public void Activate() => _eventSink.PostEvent("host.window.activate", null);

    public void Hide() => _eventSink.PostEvent("host.window.hide", null);

    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        cancellationToken.ThrowIfCancellationRequested();
        if (!uri.IsAbsoluteUri
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Only absolute HTTP(S) URIs may be opened.", nameof(uri));
        }

        _eventSink.PostEvent("host.openExternal", new { url = uri.AbsoluteUri });
        return Task.CompletedTask;
    }

    public Task<string?> PickSaveFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default) =>
        _hostRpcClient.PickSaveFileAsync(request, cancellationToken);

    public Task<string?> PickOpenFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default) =>
        _hostRpcClient.PickOpenFileAsync(request, cancellationToken);

    public Task<bool> ConfirmAsync(
        HostConfirmationRequest request,
        CancellationToken cancellationToken = default) =>
        _hostRpcClient.ConfirmAsync(request, cancellationToken);

    public StatusStripControlState GetState() => UnavailableStatusStripState(new AppSettings());

    public StatusStripControlState Preview(AppSettings settings) => UnavailableStatusStripState(settings);

    public StatusStripControlState Recover() => UnavailableStatusStripState(new AppSettings());

    private static StatusStripControlState UnavailableStatusStripState(AppSettings settings) =>
        new(
            settings.StatusStripEnabled,
            Visible: false,
            settings.StatusStripPositionLocked,
            HasManualPosition: false,
            PositionMode: "electron",
            DisplayName: "Electron host",
            Message: "状态条由 Electron 主进程管理，当前垂直切片尚未接入。");
}
