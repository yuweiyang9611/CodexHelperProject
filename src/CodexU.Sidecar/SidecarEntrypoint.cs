using CodexU.Application;
using CodexU.Contracts;

namespace CodexU.Sidecar;

public static class SidecarEntrypoint
{
    public static async Task<int> RunAsync(
        string[] args,
        Stream standardInput,
        Stream standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        try
        {
            var options = SidecarOptions.Parse(args);
            using var transport = new LengthPrefixedJsonTransport(standardInput, standardOutput);
            var eventSink = new SidecarEventSink(transport);
            var hostRpcBroker = new SidecarHostRpcBroker(transport);
            var hostRpcClient = new SidecarHostRpcClient(hostRpcBroker);
            using var backend = await SidecarBackend.CreateAsync(
                options,
                eventSink,
                hostRpcClient,
                cancellationToken);
            var handshake = SidecarHandshake.Create(
                options.BackendVersion,
                SidecarOptions.SidecarCapabilities);
            var server = new SidecarServer(
                transport,
                handshake,
                hostRpcBroker,
                backend.UpdateGlobalHotKeyRegistered,
                message => backend.RequestProcessor.ProcessAsync(
                    "https://app.codexu.local/",
                    message),
                backend.StopAsync);

            await standardError.WriteLineAsync(
                $"codexU sidecar {options.BackendVersion} started ({options.Platform}).");
            await server.RunAsync(cancellationToken);
            await standardError.WriteLineAsync("codexU sidecar stopped normally.");
            return 0;
        }
        catch (SidecarProtocolException exception)
        {
            await standardError.WriteLineAsync($"Sidecar protocol error: {exception.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("codexU sidecar cancelled.");
            return 0;
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync($"codexU sidecar failed: {exception}");
            return 1;
        }
    }
}

public sealed record SidecarOptions(
    string ApplicationDataDirectory,
    string AppVersion,
    string BackendVersion,
    string Platform,
    bool IsPackaged)
{
    public static IReadOnlyList<string> SidecarCapabilities { get; } =
    [
        "ipc.request.v1",
        "ipc.event.v1",
        "host.rpc.v1",
        "host.state.v1",
        "applicationSession",
        "gracefulShutdown"
    ];

    private static IReadOnlyList<string> SharedHostCapabilities { get; } =
    [
        "usage",
        "runtime",
        "claudeCode",
        "combinedRuntime",
        "localOnly",
        "updates",
        "localData",
        "diagnostics",
        "rateCatalog",
        "todos",
        HostCapabilityNames.NativeDialogs,
        HostCapabilityNames.AlwaysOnTop,
        HostCapabilityNames.CompactMode,
        "sidecar"
    ];

    public static IReadOnlyList<string> ResolveHostCapabilities(string platform, bool isPackaged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var capabilities = new List<string>(SharedHostCapabilities);
        if (string.Equals(platform, "windows", StringComparison.OrdinalIgnoreCase))
        {
            capabilities.Add(HostCapabilityNames.Tray);
            capabilities.Add(HostCapabilityNames.GlobalHotKey);
            if (isPackaged)
            {
                capabilities.Add(HostCapabilityNames.StartupRegistration);
            }
        }

        return capabilities;
    }

    public static SidecarOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataDirectory = Environment.GetEnvironmentVariable("CODEXU_DATA_DIRECTORY");
        string? appVersion = null;
        var isPackaged = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--data-directory":
                case "--data-dir":
                    dataDirectory = ReadValue(args, ref index);
                    break;
                case "--app-version":
                    appVersion = ReadValue(args, ref index);
                    break;
                case "--packaged":
                    isPackaged = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown sidecar argument: {args[index]}");
            }
        }

        var backendVersion = SidecarVersion.Resolve(typeof(SidecarOptions).Assembly);
        var defaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "codexU");
        var resolvedDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(dataDirectory) ? defaultDirectory : dataDirectory);
        if (!Path.IsPathFullyQualified(resolvedDirectory))
        {
            throw new ArgumentException("Sidecar data directory must resolve to an absolute path.");
        }

        return new SidecarOptions(
            resolvedDirectory,
            string.IsNullOrWhiteSpace(appVersion) ? backendVersion : appVersion.Trim(),
            backendVersion,
            ResolvePlatform(),
            isPackaged);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index)
    {
        if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"Missing value for {args[index - 1]}.");
        }

        return args[index];
    }

    private static string ResolvePlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            return "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return "unknown";
    }
}
