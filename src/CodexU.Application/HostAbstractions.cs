using CodexU.Core;

namespace CodexU.Application;

/// <summary>
/// Describes the native host without exposing WPF, Avalonia, or a particular web-view API.
/// </summary>
public interface IHostEnvironment
{
    string AppVersion { get; }

    string Platform { get; }

    string Theme { get; }

    bool IsPackaged { get; }

    IReadOnlyList<string> Capabilities { get; }

    bool IsClosing { get; }

    bool IsGlobalHotKeyRegistered { get; }

    void SignalWebReady();
}

/// <summary>
/// Receives unsolicited messages that the native host publishes to the web UI.
/// A transport may drop non-response events while the page is not ready.
/// </summary>
public interface IIpcEventSink
{
    void PostEvent(string method, object? payload);
}

public interface IStartupRegistration
{
    Task ApplyAsync(bool enabled, CancellationToken cancellationToken = default);
}

public interface IStatusStripCommands
{
    StatusStripControlState GetState();

    StatusStripControlState Preview(AppSettings settings);

    StatusStripControlState Recover();
}

public interface IWindowCommands
{
    bool IsAlwaysOnTop { get; }

    void SetAlwaysOnTop(bool enabled);

    void Show();

    void Activate();

    void Hide();
}

public interface IExternalUriLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public interface IUserInteractionService
{
    Task<string?> PickSaveFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default);

    Task<string?> PickOpenFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmAsync(
        HostConfirmationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record HostFileDialogRequest(
    string Title,
    string SuggestedFileName,
    string DefaultExtension,
    IReadOnlyList<HostFileType> FileTypes,
    bool CheckFileExists = false,
    bool OverwritePrompt = false);

public sealed record HostFileType(
    string Name,
    IReadOnlyList<string> Patterns);

public sealed record HostConfirmationRequest(
    string Title,
    string Message,
    bool IsWarning = false);

public sealed record ProjectionWarning(
    string Area,
    string Message,
    IReadOnlyList<Exception> Failures);
