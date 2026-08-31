using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using CodexU.Application;

namespace CodexU.Sidecar;

public static class SidecarHostRpcMethods
{
    public const string PickSaveFile = "host.dialog.saveFile";

    public const string PickOpenFile = "host.dialog.openFile";

    public const string Confirm = "host.dialog.confirm";

    public const string SetStartupRegistration = "host.startup.set";

    public static FrozenSet<string> Allowed { get; } = FrozenSet.Create(
        StringComparer.Ordinal,
        PickSaveFile,
        PickOpenFile,
        Confirm,
        SetStartupRegistration);
}

public sealed record SidecarHostRequest(
    int Version,
    string Id,
    string Type,
    string Method,
    JsonElement Payload)
{
    public static SidecarHostRequest Create(string id, string method, JsonElement payload) =>
        new(SidecarProtocol.Version, id, "hostRequest", method, payload);
}

public sealed class SidecarHostRpcException : Exception
{
    public SidecarHostRpcException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public enum SidecarHostResponseStatus
{
    Handled,
    UnknownCorrelationId,
    Malformed
}

public sealed record SidecarHostResponseResult(
    SidecarHostResponseStatus Status,
    string Code,
    string Message)
{
    public static SidecarHostResponseResult Handled() =>
        new(SidecarHostResponseStatus.Handled, string.Empty, string.Empty);

    public static SidecarHostResponseResult Unknown(string id) =>
        new(
            SidecarHostResponseStatus.UnknownCorrelationId,
            "unknown_host_response",
            $"No pending host request has correlation ID '{id}'.");

    public static SidecarHostResponseResult Malformed(string message) =>
        new(SidecarHostResponseStatus.Malformed, "invalid_host_response", message);
}

/// <summary>
/// Owns reverse-RPC correlation on the shared sidecar transport. The server read
/// loop supplies responses while application request tasks independently await
/// their matching host interaction.
/// </summary>
public sealed class SidecarHostRpcBroker
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

    private readonly LengthPrefixedJsonTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<string, PendingHostRequest> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stoppingCancellation = new();
    private int _stopped;

    public SidecarHostRpcBroker(
        LengthPrefixedJsonTransport transport,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var resolvedTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (resolvedTimeout <= TimeSpan.Zero && resolvedTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _transport = transport;
        _requestTimeout = resolvedTimeout;
    }

    public int PendingCount => _pending.Count;

    public async Task<JsonElement?> InvokeAsync(
        string method,
        object payload,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(payload);
        if (!SidecarHostRpcMethods.Allowed.Contains(method))
        {
            throw new NotSupportedException($"Host RPC method is not allowed: {method}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfStopped();
        var resolvedRequestTimeout = requestTimeout ?? _requestTimeout;
        if (resolvedRequestTimeout <= TimeSpan.Zero
            && resolvedRequestTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }
        var normalizedPayload = JsonSerializer.SerializeToElement(payload, IpcJson.Options);
        if (normalizedPayload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Host RPC payload must serialize to a JSON object.", nameof(payload));
        }

        var id = Guid.NewGuid().ToString("N");
        var pending = new PendingHostRequest();
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Could not allocate a host RPC correlation ID.");
        }

        if (Volatile.Read(ref _stopped) != 0)
        {
            CancelPending(id, pending);
            ThrowIfStopped();
        }

        using var timeoutCancellation = CreateTimeoutCancellation(resolvedRequestTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token,
            _stoppingCancellation.Token);
        try
        {
            await _transport.WriteFrameAsync(
                SidecarHostRequest.Create(id, method, normalizedPayload),
                IpcJson.Options,
                linkedCancellation.Token);
            return await pending.Completion.Task.WaitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException exception)
            when (timeoutCancellation.IsCancellationRequested
                  && !cancellationToken.IsCancellationRequested
                  && Volatile.Read(ref _stopped) == 0)
        {
            throw new TimeoutException(
                $"Host RPC '{method}' timed out after {resolvedRequestTimeout}.",
                exception);
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<string, PendingHostRequest>(id, pending));
        }
    }

    public SidecarHostResponseResult HandleResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            return SidecarHostResponseResult.Malformed("Host response must be a JSON object.");
        }

        if (!TryReadCorrelationId(response, out var id))
        {
            return SidecarHostResponseResult.Malformed(
                "Host response must contain a non-empty string id of at most 128 characters.");
        }

        if (!_pending.TryGetValue(id, out var pending))
        {
            return SidecarHostResponseResult.Unknown(id);
        }

        if (!TryParseResponse(response, out var payload, out var error, out var validationError))
        {
            if (_pending.TryRemove(new KeyValuePair<string, PendingHostRequest>(id, pending)))
            {
                pending.Completion.TrySetException(
                    new SidecarHostRpcException("invalid_host_response", validationError));
            }

            return SidecarHostResponseResult.Malformed(validationError);
        }

        if (!_pending.TryRemove(new KeyValuePair<string, PendingHostRequest>(id, pending)))
        {
            return SidecarHostResponseResult.Unknown(id);
        }

        if (error is not null)
        {
            pending.Completion.TrySetException(error);
        }
        else
        {
            pending.Completion.TrySetResult(payload);
        }

        return SidecarHostResponseResult.Handled();
    }

    /// <summary>
    /// If an otherwise unsupported envelope carries the ID of a pending host
    /// request, fail that request immediately instead of leaving it to time out.
    /// </summary>
    public bool RejectMalformedResponse(JsonElement message, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (message.ValueKind != JsonValueKind.Object
            || !TryReadCorrelationId(message, out var id)
            || !_pending.TryRemove(id, out var pending))
        {
            return false;
        }

        pending.Completion.TrySetException(
            new SidecarHostRpcException("invalid_host_response", reason));
        return true;
    }

    public void CancelPending()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _stoppingCancellation.Cancel();
        foreach (var entry in _pending)
        {
            CancelPending(entry.Key, entry.Value);
        }
    }

    private static bool TryParseResponse(
        JsonElement response,
        out JsonElement? payload,
        out SidecarHostRpcException? error,
        out string validationError)
    {
        payload = null;
        error = null;

        if (!response.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var versionNumber)
            || versionNumber != SidecarProtocol.Version)
        {
            validationError = "Host response has an unsupported protocol version.";
            return false;
        }

        if (!response.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "hostResponse", StringComparison.Ordinal))
        {
            validationError = "Host response type must be 'hostResponse'.";
            return false;
        }

        if (!response.TryGetProperty("ok", out var ok)
            || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            validationError = "Host response must contain a boolean ok value.";
            return false;
        }

        if (ok.GetBoolean())
        {
            if (response.TryGetProperty("error", out _))
            {
                validationError = "A successful host response cannot contain an error.";
                return false;
            }

            if (response.TryGetProperty("payload", out var payloadElement))
            {
                payload = payloadElement.Clone();
            }

            validationError = string.Empty;
            return true;
        }

        if (response.TryGetProperty("payload", out _))
        {
            validationError = "A failed host response cannot contain a payload.";
            return false;
        }

        if (!response.TryGetProperty("error", out var errorElement)
            || errorElement.ValueKind != JsonValueKind.Object
            || !TryReadBoundedString(errorElement, "code", 128, out var code)
            || !TryReadBoundedString(errorElement, "message", 4096, out var message))
        {
            validationError = "A failed host response must contain error.code and error.message strings.";
            return false;
        }

        error = new SidecarHostRpcException(code, message);
        validationError = string.Empty;
        return true;
    }

    private static bool TryReadCorrelationId(JsonElement response, out string id) =>
        TryReadBoundedString(response, "id", 128, out id);

    private static bool TryReadBoundedString(
        JsonElement value,
        string propertyName,
        int maximumLength,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > maximumLength)
        {
            return false;
        }

        result = candidate;
        return true;
    }

    private static CancellationTokenSource CreateTimeoutCancellation(TimeSpan requestTimeout) =>
        requestTimeout == Timeout.InfiniteTimeSpan
            ? new CancellationTokenSource()
            : new CancellationTokenSource(requestTimeout);

    private void CancelPending(string id, PendingHostRequest pending)
    {
        if (_pending.TryRemove(new KeyValuePair<string, PendingHostRequest>(id, pending)))
        {
            pending.Completion.TrySetCanceled();
        }
    }

    private void ThrowIfStopped()
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            throw new OperationCanceledException("The sidecar host RPC broker is stopping.");
        }
    }

    private sealed class PendingHostRequest
    {
        public TaskCompletionSource<JsonElement?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public interface ISidecarHostRpcClient
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

    Task<bool> SetStartupRegistrationAsync(
        bool enabled,
        CancellationToken cancellationToken = default);
}

public sealed class SidecarHostRpcClient : ISidecarHostRpcClient
{
    public static readonly TimeSpan DefaultStartupRegistrationTimeout = TimeSpan.FromSeconds(25);

    private readonly SidecarHostRpcBroker _broker;
    private readonly TimeSpan _startupRegistrationTimeout;

    public SidecarHostRpcClient(
        SidecarHostRpcBroker broker,
        TimeSpan? startupRegistrationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var resolvedTimeout = startupRegistrationTimeout ?? DefaultStartupRegistrationTimeout;
        if (resolvedTimeout <= TimeSpan.Zero || resolvedTimeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(startupRegistrationTimeout));
        }

        _broker = broker;
        _startupRegistrationTimeout = resolvedTimeout;
    }

    public async Task<string?> PickSaveFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadOptionalPath(
            await _broker.InvokeAsync(
                SidecarHostRpcMethods.PickSaveFile,
                request,
                cancellationToken));
    }

    public async Task<string?> PickOpenFileAsync(
        HostFileDialogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadOptionalPath(
            await _broker.InvokeAsync(
                SidecarHostRpcMethods.PickOpenFile,
                request,
                cancellationToken));
    }

    public async Task<bool> ConfirmAsync(
        HostConfirmationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = await _broker.InvokeAsync(
            SidecarHostRpcMethods.Confirm,
            request,
            cancellationToken);
        if (payload is { ValueKind: JsonValueKind.True or JsonValueKind.False })
        {
            return payload.Value.GetBoolean();
        }

        throw InvalidPayload(SidecarHostRpcMethods.Confirm, "a boolean");
    }

    public async Task<bool> SetStartupRegistrationAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var payload = await _broker.InvokeAsync(
            SidecarHostRpcMethods.SetStartupRegistration,
            new { enabled },
            cancellationToken,
            _startupRegistrationTimeout);
        if (payload is { ValueKind: JsonValueKind.True or JsonValueKind.False })
        {
            return payload.Value.GetBoolean();
        }

        throw InvalidPayload(SidecarHostRpcMethods.SetStartupRegistration, "a boolean");
    }

    private static string? ReadOptionalPath(JsonElement? payload)
    {
        if (payload is null)
        {
            throw InvalidPayload("file dialog", "a string or null");
        }

        if (payload.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (payload.Value.ValueKind == JsonValueKind.String)
        {
            return payload.Value.GetString();
        }

        throw InvalidPayload("file dialog", "a string or null");
    }

    private static SidecarHostRpcException InvalidPayload(string method, string expected) =>
        new(
            "invalid_host_response",
            $"Host RPC '{method}' returned an invalid payload; expected {expected}.");
}
