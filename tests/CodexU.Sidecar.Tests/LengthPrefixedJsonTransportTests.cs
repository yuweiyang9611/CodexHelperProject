using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CodexU.Sidecar.Tests;

public sealed class LengthPrefixedJsonTransportTests
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task WriteFrameUsesLittleEndianUtf8ByteLength()
    {
        using var output = new MemoryStream();
        using var transport = new LengthPrefixedJsonTransport(Stream.Null, output);

        await transport.WriteFrameAsync(new { message = "你好, Electron" }, JsonOptions);

        var frame = output.ToArray();
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(0, sizeof(uint)));
        Assert.Equal(frame.Length - sizeof(uint), (int)payloadLength);
        using var payload = JsonDocument.Parse(frame.AsMemory(sizeof(uint)));
        Assert.Equal("你好, Electron", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReadFrameHandlesFragmentedHeaderAndPayload()
    {
        var encoded = EncodeFrame(new { version = 1, type = "request", id = "fragmented" });
        using var input = new ChunkedReadStream(encoded, maximumChunkSize: 1);
        using var transport = new LengthPrefixedJsonTransport(input, Stream.Null);

        using var frame = await transport.ReadFrameAsync();

        Assert.NotNull(frame);
        Assert.Equal("fragmented", frame.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task EmptyInputIsGracefulEof()
    {
        using var transport = new LengthPrefixedJsonTransport(new MemoryStream(), Stream.Null);

        var frame = await transport.ReadFrameAsync();

        Assert.Null(frame);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task TruncatedHeaderIsProtocolError(int byteCount)
    {
        using var input = new MemoryStream(new byte[byteCount]);
        using var transport = new LengthPrefixedJsonTransport(input, Stream.Null);

        await Assert.ThrowsAsync<SidecarProtocolException>(async () =>
            await transport.ReadFrameAsync());
    }

    [Fact]
    public async Task TruncatedPayloadIsProtocolError()
    {
        var bytes = new byte[sizeof(uint) + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 10);
        using var transport = new LengthPrefixedJsonTransport(new MemoryStream(bytes), Stream.Null);

        await Assert.ThrowsAsync<SidecarProtocolException>(async () =>
            await transport.ReadFrameAsync());
    }

    [Fact]
    public async Task ZeroLengthFrameIsProtocolError()
    {
        using var transport = new LengthPrefixedJsonTransport(
            new MemoryStream(new byte[sizeof(uint)]),
            Stream.Null);

        await Assert.ThrowsAsync<SidecarProtocolException>(async () =>
            await transport.ReadFrameAsync());
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeAllocation()
    {
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            SidecarProtocol.MaximumFrameBytes + 1U);
        using var transport = new LengthPrefixedJsonTransport(new MemoryStream(header), Stream.Null);

        var exception = await Assert.ThrowsAsync<SidecarProtocolException>(async () =>
            await transport.ReadFrameAsync());

        Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidUtf8JsonIsProtocolError()
    {
        var bytes = new byte[sizeof(uint) + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        bytes[4] = 0xc3;
        bytes[5] = 0x28;
        using var transport = new LengthPrefixedJsonTransport(new MemoryStream(bytes), Stream.Null);

        await Assert.ThrowsAsync<SidecarProtocolException>(async () =>
            await transport.ReadFrameAsync());
    }

    [Fact]
    public async Task ConcurrentWritesRemainWholeFrames()
    {
        using var output = new MemoryStream();
        using var writer = new LengthPrefixedJsonTransport(Stream.Null, output);
        var writes = Enumerable.Range(0, 40)
            .Select(index => writer.WriteFrameAsync(new { index }, JsonOptions).AsTask())
            .ToArray();

        await Task.WhenAll(writes);
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        var observed = new HashSet<int>();
        while (await reader.ReadFrameAsync() is { } frame)
        {
            using (frame)
            {
                observed.Add(frame.RootElement.GetProperty("index").GetInt32());
            }
        }

        Assert.Equal(Enumerable.Range(0, 40), observed.Order());
    }

    [Fact]
    public async Task CancellationAfterHeaderDoesNotLeaveTruncatedFrame()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new CancelAfterFirstWriteStream(cancellation);
        using var writer = new LengthPrefixedJsonTransport(Stream.Null, output);

        await writer.WriteFrameAsync(
            new { message = "complete" },
            JsonOptions,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        output.Position = 0;
        using var reader = new LengthPrefixedJsonTransport(output, Stream.Null);
        using var frame = await reader.ReadFrameAsync();
        Assert.Equal("complete", frame!.RootElement.GetProperty("message").GetString());
        Assert.Null(await reader.ReadFrameAsync());
    }

    private static byte[] EncodeFrame<T>(T message)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var frame = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(uint)));
        return frame;
    }

    private sealed class ChunkedReadStream(byte[] contents, int maximumChunkSize) : Stream
    {
        private readonly MemoryStream _inner = new(contents);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, Math.Min(count, maximumChunkSize));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumChunkSize)], cancellationToken);

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CancelAfterFirstWriteStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private int _writeCount;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = base.WriteAsync(buffer, cancellationToken);
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                cancellation.Cancel();
            }

            return write;
        }
    }
}
