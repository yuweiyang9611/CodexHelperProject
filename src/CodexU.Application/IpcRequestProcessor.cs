using System.Text.Json;
using CodexU.Contracts;

namespace CodexU.Application;

/// <summary>
/// Validates and executes one raw message received from an embedded web view.
/// Invalid or untrusted messages intentionally produce no response.
/// </summary>
public sealed class IpcRequestProcessor(
    IpcDispatcher dispatcher,
    IHostEnvironment hostEnvironment,
    IpcRequestRoute requestRoute = IpcRequestRoute.Renderer)
{
    public const int MaximumMessageLength = 1024 * 1024;

    private readonly IpcDispatcher _dispatcher =
        dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly IHostEnvironment _hostEnvironment =
        hostEnvironment ?? throw new ArgumentNullException(nameof(hostEnvironment));
    private readonly IpcRequestRoute _requestRoute = requestRoute;

    public async Task<IpcResponse?> ProcessAsync(string? source, string? messageJson)
    {
        if (!IpcSecurityPolicy.IsTrustedSource(source)
            || string.IsNullOrEmpty(messageJson)
            || messageJson.Length > MaximumMessageLength)
        {
            return null;
        }

        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(messageJson, IpcJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (!IpcSecurityPolicy.IsValidRequest(request))
        {
            return null;
        }

        try
        {
            var payload = await _dispatcher.DispatchAsync(request, _requestRoute);
            return IpcResponse.Success(request.Id, payload);
        }
        catch (NotSupportedException exception)
        {
            return IpcResponse.Failure(request.Id, "method_not_allowed", exception.Message);
        }
        catch (OperationCanceledException exception)
        {
            return _hostEnvironment.IsClosing
                ? null
                : IpcResponse.Failure(request.Id, "operation_cancelled", exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return IpcResponse.Failure(request.Id, "host_error", exception.Message);
        }
    }
}

public enum IpcRequestRoute
{
    Renderer,
    ElectronHost
}
