using System.Text.Json;
using System.Runtime.ExceptionServices;
using CodexU.Application;
using CodexU.Contracts;

namespace CodexU.Sidecar;

/// <summary>
/// Hosts the sidecar control protocol and forwards established IPC requests to the
/// application request processor. Requests may complete out of order and retain
/// their existing correlation IDs.
/// </summary>
public sealed class SidecarServer(
    LengthPrefixedJsonTransport transport,
    SidecarHandshake handshake,
    SidecarHostRpcBroker hostRpcBroker,
    Action<bool> hostStateHandler,
    Func<string, Task<IpcResponse?>> requestHandler,
    Func<Task> stoppingCallback)
{
    private const int MaximumPendingRequests = 32;

    private readonly LengthPrefixedJsonTransport _transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private readonly SidecarHandshake _handshake =
        handshake ?? throw new ArgumentNullException(nameof(handshake));
    private readonly SidecarHostRpcBroker _hostRpcBroker =
        hostRpcBroker ?? throw new ArgumentNullException(nameof(hostRpcBroker));
    private readonly Action<bool> _hostStateHandler =
        hostStateHandler ?? throw new ArgumentNullException(nameof(hostStateHandler));
    private readonly Func<string, Task<IpcResponse?>> _requestHandler =
        requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
    private readonly Func<Task> _stoppingCallback =
        stoppingCallback ?? throw new ArgumentNullException(nameof(stoppingCallback));

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await _transport.WriteFrameAsync(_handshake, IpcJson.Options, cancellationToken);

        var pending = new List<Task>(MaximumPendingRequests);
        using var stoppingSignal = new CancellationTokenSource();
        var shutdownRequested = false;
        try
        {
            while (true)
            {
                using var frame = await _transport.ReadFrameAsync(cancellationToken);
                if (frame is null)
                {
                    break;
                }

                var root = frame.RootElement;
                if (IsShutdown(root))
                {
                    shutdownRequested = true;
                    break;
                }

                if (IsHostState(root))
                {
                    if (TryReadHostState(root, out var globalHotKeyRegistered, out var validationError))
                    {
                        _hostStateHandler(globalHotKeyRegistered);
                    }
                    else
                    {
                        await WriteProtocolErrorAsync(
                            "invalid_host_state",
                            validationError,
                            cancellationToken);
                    }

                    continue;
                }

                if (IsHostResponse(root))
                {
                    var result = _hostRpcBroker.HandleResponse(root);
                    if (result.Status != SidecarHostResponseStatus.Handled)
                    {
                        await WriteProtocolErrorAsync(result.Code, result.Message, cancellationToken);
                    }

                    continue;
                }

                if (!IsRequest(root))
                {
                    const string unsupportedMessage =
                        "Expected an IPC request, a host response, or the shutdown control message.";
                    _hostRpcBroker.RejectMalformedResponse(root, unsupportedMessage);
                    await WriteProtocolErrorAsync(
                        "unsupported_message",
                        unsupportedMessage,
                        cancellationToken);
                    continue;
                }

                var requestJson = root.GetRawText();
                RemoveCompleted(pending);
                if (pending.Count >= MaximumPendingRequests)
                {
                    await WriteCapacityErrorAsync(requestJson, cancellationToken);
                    continue;
                }

                pending.Add(ProcessRequestAsync(
                    requestJson,
                    cancellationToken,
                    stoppingSignal.Token));
            }
        }
        finally
        {
            Task? stoppingTask = null;
            Exception? stoppingFailure = null;
            try
            {
                // Invoking an async callback runs its synchronous prefix first;
                // SidecarBackend uses that prefix to mark the host as closing.
                // Do not await yet because shutdown preparation may itself be
                // waiting for a request that is blocked on reverse RPC.
                stoppingTask = _stoppingCallback()
                    ?? throw new InvalidOperationException("The stopping callback returned no task.");
            }
            catch (Exception exception)
            {
                stoppingFailure = exception;
            }

            _hostRpcBroker.CancelPending();
            stoppingSignal.Cancel();

            if (stoppingTask is not null)
            {
                try
                {
                    await stoppingTask;
                }
                catch (Exception exception)
                {
                    stoppingFailure = exception;
                }
            }

            try
            {
                await Task.WhenAll(pending);
            }
            catch (OperationCanceledException) when (stoppingSignal.IsCancellationRequested)
            {
                // A clean shutdown/EOF cancels host interactions still awaited by
                // renderer requests. Those requests no longer need a response.
            }

            if (stoppingFailure is not null)
            {
                ExceptionDispatchInfo.Capture(stoppingFailure).Throw();
            }
        }

        if (shutdownRequested)
        {
            await _transport.WriteFrameAsync(
                SidecarControlMessage.ShutdownAcknowledgement(),
                IpcJson.Options,
                cancellationToken);
        }
    }

    private async Task ProcessRequestAsync(
        string requestJson,
        CancellationToken cancellationToken,
        CancellationToken stoppingToken)
    {
        var response = await _requestHandler(requestJson);
        if (response is not null)
        {
            await _transport.WriteFrameAsync(response, IpcJson.Options, cancellationToken);
            return;
        }

        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var requestId = TryReadRequestId(requestJson);
        if (requestId is not null)
        {
            await _transport.WriteFrameAsync(
                IpcResponse.Failure(requestId, "invalid_request", "IPC request envelope is invalid."),
                IpcJson.Options,
                cancellationToken);
            return;
        }

        await WriteProtocolErrorAsync(
            "invalid_request",
            "IPC request envelope is invalid and has no usable correlation ID.",
            cancellationToken);
    }

    private async Task WriteCapacityErrorAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        var requestId = TryReadRequestId(requestJson);
        if (requestId is not null)
        {
            await _transport.WriteFrameAsync(
                IpcResponse.Failure(
                    requestId,
                    "server_busy",
                    $"The sidecar already has {MaximumPendingRequests} pending requests."),
                IpcJson.Options,
                cancellationToken);
            return;
        }

        await WriteProtocolErrorAsync(
            "server_busy",
            $"The sidecar already has {MaximumPendingRequests} pending requests.",
            cancellationToken);
    }

    private ValueTask WriteProtocolErrorAsync(
        string code,
        string message,
        CancellationToken cancellationToken) =>
        _transport.WriteFrameAsync(
            IpcEvent.Create("sidecar.protocolError", new { code, message }),
            IpcJson.Options,
            cancellationToken);

    private static bool IsRequest(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "request", StringComparison.Ordinal);

    private static bool IsHostResponse(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "hostResponse", StringComparison.Ordinal);

    private static bool IsHostState(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "hostState", StringComparison.Ordinal);

    private static bool TryReadHostState(
        JsonElement root,
        out bool globalHotKeyRegistered,
        out string validationError)
    {
        const int versionField = 1 << 0;
        const int typeField = 1 << 1;
        const int hotKeyField = 1 << 2;
        const int allFields = versionField | typeField | hotKeyField;

        globalHotKeyRegistered = false;
        var observedFields = 0;
        foreach (var property in root.EnumerateObject())
        {
            var field = property.Name switch
            {
                "version" => versionField,
                "type" => typeField,
                "globalHotKeyRegistered" => hotKeyField,
                _ => 0
            };
            if (field == 0 || (observedFields & field) != 0)
            {
                validationError =
                    "Host state must contain exactly version, type, and globalHotKeyRegistered.";
                return false;
            }

            observedFields |= field;
        }

        if (observedFields != allFields
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber)
            || versionNumber != SidecarProtocol.Version
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "hostState", StringComparison.Ordinal)
            || !root.TryGetProperty("globalHotKeyRegistered", out var registered)
            || registered.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            validationError =
                "Host state requires version 1, type 'hostState', and a boolean globalHotKeyRegistered.";
            return false;
        }

        globalHotKeyRegistered = registered.GetBoolean();
        validationError = string.Empty;
        return true;
    }

    private static bool IsShutdown(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("version", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var versionNumber)
        && versionNumber == SidecarProtocol.Version
        && root.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "control", StringComparison.Ordinal)
        && root.TryGetProperty("method", out var method)
        && method.ValueKind == JsonValueKind.String
        && string.Equals(method.GetString(), SidecarProtocol.ShutdownMethod, StringComparison.Ordinal);

    private static string? TryReadRequestId(string requestJson)
    {
        try
        {
            using var request = JsonDocument.Parse(requestJson);
            if (!request.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = id.GetString();
            return value is { Length: > 0 and <= 128 } ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RemoveCompleted(List<Task> pending)
    {
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            if (pending[index].IsCompleted)
            {
                pending[index].GetAwaiter().GetResult();
                pending.RemoveAt(index);
            }
        }
    }
}
