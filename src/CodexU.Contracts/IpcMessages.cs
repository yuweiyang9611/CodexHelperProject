using System.Text.Json;

namespace CodexU.Contracts;

public sealed record IpcRequest(
    int Version,
    string Id,
    string Type,
    string Method,
    JsonElement Payload);

public sealed record IpcResponse(
    int Version,
    string Id,
    string Type,
    bool Ok,
    object? Payload,
    IpcError? Error = null)
{
    public static IpcResponse Success(string id, object? payload) =>
        new(1, id, "response", true, payload);

    public static IpcResponse Failure(string id, string code, string message) =>
        new(1, id, "response", false, null, new IpcError(code, message));
}

public sealed record IpcEvent(
    int Version,
    string Type,
    string Method,
    object? Payload)
{
    public static IpcEvent Create(string method, object? payload) =>
        new(1, "event", method, payload);
}

public sealed record IpcError(string Code, string Message);

public sealed record InitializeResult(
    string AppVersion,
    string Platform,
    string Theme,
    bool IsPackaged,
    IReadOnlyList<string> Capabilities);

public sealed record RuntimeSelection(string Runtime);

public sealed record WindowVisibilityRequest(bool Visible);

public sealed record AlwaysOnTopRequest(bool Enabled);
