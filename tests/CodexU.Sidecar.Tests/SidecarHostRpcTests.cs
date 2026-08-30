using System.Text.Json;
using System.Threading.Channels;
using CodexU.Application;
using CodexU.Contracts;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarHostRpcTests
{
    // This guard catches deadlocks without failing when a shared Windows runner
    // temporarily suspends the test process under parallel solution load.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FileDialogUsesFixedSchemaAndDirectStringPayload()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);
        var client = new SidecarHostRpcClient(broker);
        var request = new HostFileDialogRequest(
            "Export",
            "report.json",
            ".json",
            [new HostFileType("JSON", ["*.json"])],
            CheckFileExists: false,
            OverwritePrompt: true);

        var invocation = client.PickSaveFileAsync(request);
        using var hostRequest = await ReadWrittenFrameAsync(output);

        var root = hostRequest.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("hostRequest", root.GetProperty("type").GetString());
        Assert.Equal("host.dialog.saveFile", root.GetProperty("method").GetString());
        var payload = root.GetProperty("payload");
        Assert.Equal("Export", payload.GetProperty("title").GetString());
        Assert.Equal("report.json", payload.GetProperty("suggestedFileName").GetString());
        Assert.Equal(".json", payload.GetProperty("defaultExtension").GetString());
        Assert.False(payload.GetProperty("checkFileExists").GetBoolean());
        Assert.True(payload.GetProperty("overwritePrompt").GetBoolean());
        Assert.Equal("JSON", payload.GetProperty("fileTypes")[0].GetProperty("name").GetString());
        Assert.Equal("*.json", payload.GetProperty("fileTypes")[0].GetProperty("patterns")[0].GetString());

        using var response = Response(
            root.GetProperty("id").GetString()!,
            ok: true,
            payload: "C:\\exports\\report.json");
        Assert.Equal(
            SidecarHostResponseStatus.Handled,
            broker.HandleResponse(response.RootElement).Status);
        Assert.Equal("C:\\exports\\report.json", await invocation.WaitAsync(TestTimeout));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task FileDialogNullPayloadMeansCancellation()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);
        var client = new SidecarHostRpcClient(broker);

        var invocation = client.PickOpenFileAsync(FileRequest());
        using var request = await ReadWrittenFrameAsync(output);
        using var response = Response(
            request.RootElement.GetProperty("id").GetString()!,
            ok: true,
            payload: null);

        broker.HandleResponse(response.RootElement);

        Assert.Null(await invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task FileDialogMissingPayloadIsRejectedRatherThanTreatedAsCancellation()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);
        var client = new SidecarHostRpcClient(broker);

        var invocation = client.PickOpenFileAsync(FileRequest());
        using var request = await ReadWrittenFrameAsync(output);
        using var response = JsonSerializer.SerializeToDocument(new
        {
            version = 1,
            id = request.RootElement.GetProperty("id").GetString(),
            type = "hostResponse",
            ok = true
        });

        Assert.Equal(
            SidecarHostResponseStatus.Handled,
            broker.HandleResponse(response.RootElement).Status);
        var exception = await Assert.ThrowsAsync<SidecarHostRpcException>(async () =>
            await invocation.WaitAsync(TestTimeout));
        Assert.Equal("invalid_host_response", exception.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmationUsesDirectBooleanPayload(bool confirmed)
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);
        var client = new SidecarHostRpcClient(broker);

        var invocation = client.ConfirmAsync(new HostConfirmationRequest("Reset", "Continue?", true));
        using var request = await ReadWrittenFrameAsync(output);
        Assert.Equal("host.dialog.confirm", request.RootElement.GetProperty("method").GetString());
        Assert.True(request.RootElement.GetProperty("payload").GetProperty("isWarning").GetBoolean());
        using var response = Response(
            request.RootElement.GetProperty("id").GetString()!,
            ok: true,
            payload: confirmed);

        broker.HandleResponse(response.RootElement);

        Assert.Equal(confirmed, await invocation.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task HostErrorCompletesMatchingRequestWithCodeAndMessage()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        var invocation = broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { });
        using var request = await ReadWrittenFrameAsync(output);
        using var response = JsonSerializer.SerializeToDocument(
            new
            {
                version = 1,
                id = request.RootElement.GetProperty("id").GetString(),
                type = "hostResponse",
                ok = false,
                error = new { code = "dialog_failed", message = "Native dialog failed." }
            },
            IpcJson.Options);

        broker.HandleResponse(response.RootElement);

        var exception = await Assert.ThrowsAsync<SidecarHostRpcException>(async () =>
            await invocation.WaitAsync(TestTimeout));
        Assert.Equal("dialog_failed", exception.Code);
        Assert.Equal("Native dialog failed.", exception.Message);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task MalformedMatchingResponseFailsRequestWithoutWaitingForTimeout()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport, TimeSpan.FromMinutes(5));

        var invocation = broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { });
        using var request = await ReadWrittenFrameAsync(output);
        using var response = JsonSerializer.SerializeToDocument(new
        {
            version = 1,
            id = request.RootElement.GetProperty("id").GetString(),
            type = "hostResponse"
        });

        var result = broker.HandleResponse(response.RootElement);

        Assert.Equal(SidecarHostResponseStatus.Malformed, result.Status);
        var exception = await Assert.ThrowsAsync<SidecarHostRpcException>(async () =>
            await invocation.WaitAsync(TestTimeout));
        Assert.Equal("invalid_host_response", exception.Code);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task UnknownAndDuplicateResponsesDoNotDisturbOtherPendingRequests()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        var invocation = broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { });
        using var request = await ReadWrittenFrameAsync(output);
        var id = request.RootElement.GetProperty("id").GetString()!;
        using var unknown = Response("unknown-id", ok: true, payload: true);
        using var valid = Response(id, ok: true, payload: false);

        Assert.Equal(
            SidecarHostResponseStatus.UnknownCorrelationId,
            broker.HandleResponse(unknown.RootElement).Status);
        Assert.Equal(1, broker.PendingCount);
        Assert.Equal(SidecarHostResponseStatus.Handled, broker.HandleResponse(valid.RootElement).Status);
        Assert.False((await invocation.WaitAsync(TestTimeout))!.Value.GetBoolean());
        Assert.Equal(
            SidecarHostResponseStatus.UnknownCorrelationId,
            broker.HandleResponse(valid.RootElement).Status);
    }

    [Fact]
    public async Task CallerCancellationRemovesPendingRequest()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);
        using var cancellation = new CancellationTokenSource();

        var invocation = broker.InvokeAsync(
            SidecarHostRpcMethods.Confirm,
            new { },
            cancellation.Token);
        Assert.Equal(1, broker.PendingCount);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await invocation.WaitAsync(TestTimeout));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task RequestTimeoutRemovesPendingRequest()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport, TimeSpan.FromMilliseconds(50));

        var invocation = broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { });

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await invocation.WaitAsync(TestTimeout));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task StopCancelsPendingAndRejectsNewRequestsWithoutWriting()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        var pendingInvocation = broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { });
        var bytesBeforeStop = output.Length;
        broker.CancelPending();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pendingInvocation.WaitAsync(TestTimeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await broker.InvokeAsync(SidecarHostRpcMethods.Confirm, new { }));
        Assert.Equal(bytesBeforeStop, output.Length);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task DisallowedMethodIsRejectedBeforeWriting()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await broker.InvokeAsync("host.shell.execute", new { command = "whoami" }));

        Assert.Equal(0, output.Length);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task ScalarPayloadIsRejectedBeforeWriting()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await broker.InvokeAsync(SidecarHostRpcMethods.Confirm, "not-an-object"));

        Assert.Equal(0, output.Length);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task ResponsePayloadOutlivesSourceJsonDocument()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);
        var broker = new SidecarHostRpcBroker(transport);

        var invocation = broker.InvokeAsync(SidecarHostRpcMethods.PickOpenFile, new { });
        using var request = await ReadWrittenFrameAsync(output);
        var response = Response(
            request.RootElement.GetProperty("id").GetString()!,
            ok: true,
            payload: "C:\\input.json");
        broker.HandleResponse(response.RootElement);
        response.Dispose();

        var payload = await invocation.WaitAsync(TestTimeout);
        Assert.Equal("C:\\input.json", payload!.Value.GetString());
    }

    [Fact]
    public void WindowsPackagedCapabilitiesDeclareOnlyImplementedElectronFeatures()
    {
        var capabilities = SidecarOptions.ResolveHostCapabilities("windows", isPackaged: true);

        Assert.Contains("host.rpc.v1", SidecarOptions.SidecarCapabilities);
        Assert.Contains("host.state.v1", SidecarOptions.SidecarCapabilities);
        Assert.Contains(HostCapabilityNames.NativeDialogs, capabilities);
        Assert.Contains(HostCapabilityNames.Tray, capabilities);
        Assert.Contains(HostCapabilityNames.AlwaysOnTop, capabilities);
        Assert.Contains(HostCapabilityNames.GlobalHotKey, capabilities);
        Assert.Contains(HostCapabilityNames.CompactMode, capabilities);
        Assert.Contains(HostCapabilityNames.StartupRegistration, capabilities);
        Assert.DoesNotContain(HostCapabilityNames.NativeNotifications, capabilities);
        Assert.DoesNotContain(HostCapabilityNames.StatusStripControl, capabilities);
        Assert.DoesNotContain(HostCapabilityNames.DesktopMode, capabilities);
        Assert.Equal(
            new[]
            {
                "host.dialog.confirm",
                "host.dialog.openFile",
                "host.dialog.saveFile"
            },
            SidecarHostRpcMethods.Allowed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PlatformSpecificCapabilitiesFailClosedOutsideWindowsInstaller()
    {
        var windowsDevelopment = SidecarOptions.ResolveHostCapabilities("windows", isPackaged: false);
        var linuxPackaged = SidecarOptions.ResolveHostCapabilities("linux", isPackaged: true);

        Assert.DoesNotContain(HostCapabilityNames.StartupRegistration, windowsDevelopment);
        Assert.DoesNotContain(HostCapabilityNames.StartupRegistration, linuxPackaged);
        Assert.DoesNotContain(HostCapabilityNames.Tray, linuxPackaged);
        Assert.DoesNotContain(HostCapabilityNames.GlobalHotKey, linuxPackaged);
    }

    [Fact]
    public async Task ServerRoutesHostResponseWhileRendererRequestIsPending()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var stopped = false;
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                var confirmed = await client.ConfirmAsync(
                    new HostConfirmationRequest("Confirm", "Continue?"));
                return IpcResponse.Success(request.Id, new { confirmed });
            },
            () =>
            {
                stopped = true;
                return Task.CompletedTask;
            });

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        Assert.Equal("handshake", handshake.RootElement.GetProperty("type").GetString());
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        Assert.Equal("hostRequest", hostRequest.RootElement.GetProperty("type").GetString());
        await inputWriter.WriteFrameAsync(
            new
            {
                version = 1,
                id = hostRequest.RootElement.GetProperty("id").GetString(),
                type = "hostResponse",
                ok = true,
                payload = true
            },
            IpcJson.Options);
        using var rendererResponse = await ReadFrameAsync(outputReader);
        Assert.Equal("renderer-1", rendererResponse.RootElement.GetProperty("id").GetString());
        Assert.True(rendererResponse.RootElement.GetProperty("payload").GetProperty("confirmed").GetBoolean());
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);
        using var acknowledgement = await ReadFrameAsync(outputReader);

        await run.WaitAsync(TestTimeout);
        Assert.Equal("shutdownAck", acknowledgement.RootElement.GetProperty("method").GetString());
        Assert.True(stopped);
    }

    [Fact]
    public async Task MalformedHostStateDoesNotDisturbPendingHostRpc()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var environment = new SidecarHostEnvironment(
            "0.5.0",
            "windows",
            "dark",
            false,
            [],
            new NoOpEventSink());
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1", "host.state.v1"]),
            broker,
            environment.UpdateGlobalHotKeyRegistered,
            async message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                var confirmed = await client.ConfirmAsync(
                    new HostConfirmationRequest("Confirm", "Continue?"));
                return IpcResponse.Success(request.Id, new { confirmed });
            },
            () => Task.CompletedTask);

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(
            new
            {
                version = 1,
                type = "hostState",
                globalHotKeyRegistered = false,
                id = hostRequest.RootElement.GetProperty("id").GetString()
            },
            IpcJson.Options);
        using var protocolError = await ReadFrameAsync(outputReader);
        Assert.Equal("sidecar.protocolError", protocolError.RootElement.GetProperty("method").GetString());
        Assert.Equal(1, broker.PendingCount);
        Assert.True(environment.IsGlobalHotKeyRegistered);

        await inputWriter.WriteFrameAsync(
            new
            {
                version = 1,
                id = hostRequest.RootElement.GetProperty("id").GetString(),
                type = "hostResponse",
                ok = true,
                payload = true
            },
            IpcJson.Options);
        using var rendererResponse = await ReadFrameAsync(outputReader);
        Assert.True(rendererResponse.RootElement.GetProperty("payload").GetProperty("confirmed").GetBoolean());
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);
        using var acknowledgement = await ReadFrameAsync(outputReader);
        await run.WaitAsync(TestTimeout);
        Assert.Equal("shutdownAck", acknowledgement.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ShutdownCancelsRendererRequestWaitingForHostResponse()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                var confirmed = await client.ConfirmAsync(
                    new HostConfirmationRequest("Confirm", "Continue?"));
                return IpcResponse.Success(request.Id, new { confirmed });
            },
            () => Task.CompletedTask);

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        Assert.Equal("hostRequest", hostRequest.RootElement.GetProperty("type").GetString());
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);
        using var acknowledgement = await ReadFrameAsync(outputReader);

        await run.WaitAsync(TestTimeout);
        Assert.Equal("shutdownAck", acknowledgement.RootElement.GetProperty("method").GetString());
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task ShutdownCancelsHostRpcBeforeAwaitingMutationDrain()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        using var mutationGate = new SemaphoreSlim(1, 1);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var closingWasSignaled = false;
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                await mutationGate.WaitAsync();
                try
                {
                    var confirmed = await client.ConfirmAsync(
                        new HostConfirmationRequest("Confirm", "Continue?"));
                    return IpcResponse.Success(request.Id, new { confirmed });
                }
                finally
                {
                    mutationGate.Release();
                }
            },
            async () =>
            {
                closingWasSignaled = true;
                await mutationGate.WaitAsync();
                mutationGate.Release();
            });

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);
        using var acknowledgement = await ReadFrameAsync(outputReader);

        await run.WaitAsync(TestTimeout);
        Assert.True(closingWasSignaled);
        Assert.Equal("shutdownAck", acknowledgement.RootElement.GetProperty("method").GetString());
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task EofCancelsRendererRequestWaitingForHostResponse()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                try
                {
                    var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                    var confirmed = await client.ConfirmAsync(
                        new HostConfirmationRequest("Confirm", "Continue?"));
                    return IpcResponse.Success(request.Id, new { confirmed });
                }
                finally
                {
                    handlerCompleted.TrySetResult();
                }
            },
            () =>
            {
                stopped = true;
                return Task.CompletedTask;
            });

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        inputStream.CompleteWriting();

        await run.WaitAsync(TestTimeout);
        await handlerCompleted.Task.WaitAsync(TestTimeout);
        Assert.True(stopped);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task StoppingCallbackFailureStillWaitsForPendingCancellation()
    {
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                try
                {
                    var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                    var confirmed = await client.ConfirmAsync(
                        new HostConfirmationRequest("Confirm", "Continue?"));
                    return IpcResponse.Success(request.Id, new { confirmed });
                }
                finally
                {
                    handlerCompleted.TrySetResult();
                }
            },
            () => Task.FromException(new InvalidOperationException("Stop failed.")));

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(RendererRequest("renderer-1"), IpcJson.Options);
        using var hostRequest = await ReadFrameAsync(outputReader);
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await run.WaitAsync(TestTimeout));
        Assert.Equal("Stop failed.", exception.Message);
        Assert.True(handlerCompleted.Task.IsCompletedSuccessfully);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task CapacityLimitDoesNotBlockHostResponses()
    {
        const int capacity = 32;
        using var inputStream = new AsyncByteStream();
        using var outputStream = new AsyncByteStream();
        using var serverTransport = new LengthPrefixedJsonTransport(inputStream, outputStream);
        using var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, inputStream);
        using var outputReader = new LengthPrefixedJsonTransport(outputStream, Stream.Null);
        var broker = new SidecarHostRpcBroker(serverTransport);
        var client = new SidecarHostRpcClient(broker);
        var server = new SidecarServer(
            serverTransport,
            SidecarHandshake.Create("0.5.0", ["host.rpc.v1"]),
            broker,
            _ => { },
            async message =>
            {
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options)!;
                var confirmed = await client.ConfirmAsync(
                    new HostConfirmationRequest("Confirm", "Continue?"));
                return IpcResponse.Success(request.Id, new { confirmed });
            },
            () => Task.CompletedTask);

        var run = server.RunAsync();
        using var handshake = await ReadFrameAsync(outputReader);
        var hostRequestIds = new List<string>(capacity);
        for (var index = 0; index < capacity; index++)
        {
            await inputWriter.WriteFrameAsync(RendererRequest($"renderer-{index}"), IpcJson.Options);
            using var hostRequest = await ReadFrameAsync(outputReader);
            Assert.Equal("hostRequest", hostRequest.RootElement.GetProperty("type").GetString());
            hostRequestIds.Add(hostRequest.RootElement.GetProperty("id").GetString()!);
        }

        await inputWriter.WriteFrameAsync(RendererRequest("renderer-over-capacity"), IpcJson.Options);
        using var capacityResponse = await ReadFrameAsync(outputReader);
        Assert.Equal("renderer-over-capacity", capacityResponse.RootElement.GetProperty("id").GetString());
        Assert.Equal("server_busy", capacityResponse.RootElement.GetProperty("error").GetProperty("code").GetString());

        foreach (var hostRequestId in hostRequestIds)
        {
            await inputWriter.WriteFrameAsync(
                new
                {
                    version = 1,
                    id = hostRequestId,
                    type = "hostResponse",
                    ok = true,
                    payload = true
                },
                IpcJson.Options);
        }

        var rendererResponseIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < capacity; index++)
        {
            using var response = await ReadFrameAsync(outputReader);
            rendererResponseIds.Add(response.RootElement.GetProperty("id").GetString()!);
        }

        Assert.Equal(capacity, rendererResponseIds.Count);
        Assert.DoesNotContain("renderer-over-capacity", rendererResponseIds);
        await inputWriter.WriteFrameAsync(
            new { version = 1, type = "control", method = "shutdown" },
            IpcJson.Options);
        using var acknowledgement = await ReadFrameAsync(outputReader);
        await run.WaitAsync(TestTimeout);
        Assert.Equal("shutdownAck", acknowledgement.RootElement.GetProperty("method").GetString());
    }

    private static HostFileDialogRequest FileRequest() =>
        new("Open", string.Empty, ".json", [new HostFileType("JSON", ["*.json"])], true);

    private static object RendererRequest(string id) =>
        new
        {
            version = 1,
            id,
            type = "request",
            method = "test.interaction",
            payload = new { }
        };

    private static JsonDocument Response(string id, bool ok, object? payload) =>
        JsonSerializer.SerializeToDocument(
            new { version = 1, id, type = "hostResponse", ok, payload },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<JsonDocument> ReadWrittenFrameAsync(MemoryStream output)
    {
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        return (await reader.ReadFrameAsync())!;
    }

    private static async Task<JsonDocument> ReadFrameAsync(LengthPrefixedJsonTransport reader) =>
        (await reader.ReadFrameAsync().AsTask().WaitAsync(TestTimeout))!;

    private sealed class AsyncByteStream : Stream
    {
        private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            while (await _channel.Reader.WaitToReadAsync(cancellationToken))
            {
                var count = 0;
                while (count < buffer.Length && _channel.Reader.TryRead(out var value))
                {
                    buffer.Span[count++] = value;
                }

                if (count > 0)
                {
                    return count;
                }
            }

            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            for (var index = offset; index < offset + count; index++)
            {
                if (!_channel.Writer.TryWrite(buffer[index]))
                {
                    throw new IOException("The test stream is closed.");
                }
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var value in buffer.Span)
            {
                if (!_channel.Writer.TryWrite(value))
                {
                    throw new IOException("The test stream is closed.");
                }
            }

            return ValueTask.CompletedTask;
        }

        public void CompleteWriting() => _channel.Writer.TryComplete();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _channel.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NoOpEventSink : IIpcEventSink
    {
        public void PostEvent(string method, object? payload)
        {
        }
    }
}
