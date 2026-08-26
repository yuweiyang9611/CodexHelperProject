using System.Text.Json;
using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarHostStateTests
{
    [Fact]
    public void HostEnvironmentDefaultsToSuccessUntilElectronReportsActualState()
    {
        var environment = CreateEnvironment();

        Assert.True(environment.IsGlobalHotKeyRegistered);

        environment.UpdateGlobalHotKeyRegistered(false);
        Assert.False(environment.IsGlobalHotKeyRegistered);

        environment.UpdateGlobalHotKeyRegistered(true);
        Assert.True(environment.IsGlobalHotKeyRegistered);
    }

    [Fact]
    public async Task ValidHostStateIsAppliedBeforeFollowingRendererRequest()
    {
        using var input = new MemoryStream();
        using (var writer = new LengthPrefixedJsonTransport(Stream.Null, input))
        {
            await writer.WriteFrameAsync(
                new SidecarHostState(1, "hostState", GlobalHotKeyRegistered: false),
                IpcJson.Options);
            await writer.WriteFrameAsync(RendererRequest("after-state"), IpcJson.Options);
            await writer.WriteFrameAsync(Shutdown(), IpcJson.Options);
        }

        input.Position = 0;
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(input, output);
        var broker = new SidecarHostRpcBroker(transport);
        var environment = CreateEnvironment();
        var server = new SidecarServer(
            transport,
            SidecarHandshake.Create("0.5.0", ["host.state.v1"]),
            broker,
            environment.UpdateGlobalHotKeyRegistered,
            message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                return Task.FromResult<IpcResponse?>(IpcResponse.Success(
                    request.Id,
                    new { globalHotKeyRegistered = environment.IsGlobalHotKeyRegistered }));
            },
            () => Task.CompletedTask);

        await server.RunAsync();

        Assert.False(environment.IsGlobalHotKeyRegistered);
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        using var handshake = await reader.ReadFrameAsync();
        using var response = await reader.ReadFrameAsync();
        using var acknowledgement = await reader.ReadFrameAsync();
        Assert.False(
            response!.RootElement
                .GetProperty("payload")
                .GetProperty("globalHotKeyRegistered")
                .GetBoolean());
        Assert.Equal("shutdownAck", acknowledgement!.RootElement.GetProperty("method").GetString());
        Assert.Null(await reader.ReadFrameAsync());
    }

    public static TheoryData<string> MalformedHostStates => new()
    {
        "{\"version\":2,\"type\":\"hostState\",\"globalHotKeyRegistered\":false}",
        "{\"version\":1,\"type\":\"hostState\"}",
        "{\"version\":1,\"type\":\"hostState\",\"globalHotKeyRegistered\":\"false\"}",
        "{\"version\":1,\"type\":\"hostState\",\"globalHotKeyRegistered\":false,\"extra\":0}",
        "{\"version\":1,\"type\":\"hostState\",\"globalHotKeyRegistered\":false,\"globalHotKeyRegistered\":true}"
    };

    [Theory]
    [MemberData(nameof(MalformedHostStates))]
    public async Task MalformedHostStateReportsProtocolErrorAndPreservesState(string stateJson)
    {
        using var input = new MemoryStream();
        using (var writer = new LengthPrefixedJsonTransport(Stream.Null, input))
        using (var state = JsonDocument.Parse(stateJson))
        {
            await writer.WriteFrameAsync(state.RootElement, IpcJson.Options);
            await writer.WriteFrameAsync(RendererRequest("after-invalid-state"), IpcJson.Options);
            await writer.WriteFrameAsync(Shutdown(), IpcJson.Options);
        }

        input.Position = 0;
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(input, output);
        var broker = new SidecarHostRpcBroker(transport);
        var environment = CreateEnvironment();
        var server = new SidecarServer(
            transport,
            SidecarHandshake.Create("0.5.0", ["host.state.v1"]),
            broker,
            environment.UpdateGlobalHotKeyRegistered,
            message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                return Task.FromResult<IpcResponse?>(IpcResponse.Success(
                    request.Id,
                    new { globalHotKeyRegistered = environment.IsGlobalHotKeyRegistered }));
            },
            () => Task.CompletedTask);

        await server.RunAsync();

        Assert.True(environment.IsGlobalHotKeyRegistered);
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        using var handshake = await reader.ReadFrameAsync();
        using var protocolError = await reader.ReadFrameAsync();
        using var response = await reader.ReadFrameAsync();
        using var acknowledgement = await reader.ReadFrameAsync();
        Assert.Equal("sidecar.protocolError", protocolError!.RootElement.GetProperty("method").GetString());
        Assert.Equal(
            "invalid_host_state",
            protocolError.RootElement.GetProperty("payload").GetProperty("code").GetString());
        Assert.True(
            response!.RootElement
                .GetProperty("payload")
                .GetProperty("globalHotKeyRegistered")
                .GetBoolean());
        Assert.Equal("shutdownAck", acknowledgement!.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ApplicationSnapshotOnlyReportsKnownHotKeyFailure()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-host-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var environment = CreateEnvironment();
            var dashboard = new StaticDashboardService();
            using var session = new ApplicationSession(
                dashboard,
                new AppSettingsStore(dataDirectory),
                new TodoStore(dataDirectory),
                new UnusedUpdateService(),
                new AppSettings().Normalize(),
                dataDirectory,
                new NoOpStartupRegistration(),
                environment,
                _ => dashboard);

            var initial = await session.LoadCurrentRuntimeSnapshotAsync();
            Assert.DoesNotContain(
                initial.Diagnostics,
                diagnostic => diagnostic.Contains("全局快捷键", StringComparison.Ordinal));

            environment.UpdateGlobalHotKeyRegistered(false);
            var failed = await session.LoadCurrentRuntimeSnapshotAsync();
            Assert.Contains(
                failed.Diagnostics,
                diagnostic => diagnostic.Contains("全局快捷键", StringComparison.Ordinal)
                              && diagnostic.Contains("注册失败", StringComparison.Ordinal));

            environment.UpdateGlobalHotKeyRegistered(true);
            var recovered = await session.LoadCurrentRuntimeSnapshotAsync();
            Assert.DoesNotContain(
                recovered.Diagnostics,
                diagnostic => diagnostic.Contains("全局快捷键", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void HostStateIsNotPartOfRendererIpcSurface()
    {
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("hostState"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("host.state"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("host.state.update"));
    }

    private static SidecarHostEnvironment CreateEnvironment() =>
        new("0.5.0", "windows", "dark", false, [], new NoOpEventSink());

    private static object RendererRequest(string id) =>
        new
        {
            version = 1,
            id,
            type = "request",
            method = "app.initialize",
            payload = new { }
        };

    private static object Shutdown() =>
        new { version = 1, type = "control", method = "shutdown" };

    private sealed class NoOpEventSink : IIpcEventSink
    {
        public void PostEvent(string method, object? payload)
        {
        }
    }

    private sealed class StaticDashboardService : IDashboardService
    {
        public Task<DashboardSnapshot> LoadAsync(
            AgentRuntime runtime = AgentRuntime.Codex,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DashboardSnapshot.Empty(runtime, "base diagnostic"));
        }
    }

    private sealed class UnusedUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(
            string currentVersion,
            bool includePrereleases,
            bool force,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpStartupRegistration : IStartupRegistration
    {
        public void Apply(bool enabled)
        {
        }
    }
}
