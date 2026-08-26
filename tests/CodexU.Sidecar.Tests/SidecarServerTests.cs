using System.Text.Json;
using CodexU.Application;
using CodexU.Contracts;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarServerTests
{
    [Fact]
    public async Task HandshakeIsFirstAndShutdownAckIsLast()
    {
        using var input = new MemoryStream();
        using (var inputWriter = new LengthPrefixedJsonTransport(Stream.Null, input))
        {
            await inputWriter.WriteFrameAsync(
                new
                {
                    version = 1,
                    id = "initialize-1",
                    type = "request",
                    method = "app.initialize",
                    payload = new { }
                },
                IpcJson.Options);
            await inputWriter.WriteFrameAsync(
                new { version = 1, type = "control", method = "shutdown" },
                IpcJson.Options);
        }

        input.Position = 0;
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(input, output);
        var hostRpcBroker = new SidecarHostRpcBroker(transport);
        var stopped = false;
        var server = new SidecarServer(
            transport,
            SidecarHandshake.Create("0.5.0", ["test"]),
            hostRpcBroker,
            _ => { },
            async message =>
            {
                await Task.Yield();
                var request = JsonSerializer.Deserialize<IpcRequest>(message, IpcJson.Options);
                return IpcResponse.Success(request!.Id, new { initialized = true });
            },
            () =>
            {
                stopped = true;
                return Task.CompletedTask;
            });

        await server.RunAsync();

        Assert.True(stopped);
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        using var handshake = await reader.ReadFrameAsync();
        using var response = await reader.ReadFrameAsync();
        using var acknowledgement = await reader.ReadFrameAsync();
        Assert.Equal("handshake", handshake!.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, handshake.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("initialize-1", response!.RootElement.GetProperty("id").GetString());
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("shutdownAck", acknowledgement!.RootElement.GetProperty("method").GetString());
        Assert.Null(await reader.ReadFrameAsync());
    }

    [Fact]
    public async Task ProtocolViolationMakesEntrypointReturnNonZero()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-sidecar-test-{Guid.NewGuid():N}");
        var zeroLengthFrame = new byte[sizeof(uint)];
        using var input = new MemoryStream(zeroLengthFrame);
        using var output = new MemoryStream();
        using var error = new StringWriter();
        try
        {
            var exitCode = await SidecarEntrypoint.RunAsync(
                ["--data-directory", dataDirectory],
                input,
                output,
                error);

            Assert.Equal(2, exitCode);
            Assert.Contains("protocol error", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
