using System.Buffers.Binary;
using System.Text.Json;

namespace CodexU.Sidecar;

/// <summary>
/// Exchanges UTF-8 JSON values framed by a four-byte, little-endian payload length.
/// The limit is applied to encoded payload bytes rather than UTF-16 character count.
/// </summary>
public sealed class LengthPrefixedJsonTransport : IDisposable
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly int _maximumFrameBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    public LengthPrefixedJsonTransport(
        Stream input,
        Stream output,
        int maximumFrameBytes = SidecarProtocol.MaximumFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        if (!input.CanRead)
        {
            throw new ArgumentException("The transport input stream must be readable.", nameof(input));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The transport output stream must be writable.", nameof(output));
        }

        if (maximumFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
        }

        _input = input;
        _output = output;
        _maximumFrameBytes = maximumFrameBytes;
    }

    public int MaximumFrameBytes => _maximumFrameBytes;

    /// <summary>
    /// Returns <see langword="null"/> only when EOF is observed before any header byte.
    /// A truncated header or payload is a fatal protocol error.
    /// </summary>
    public async ValueTask<JsonDocument?> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = new byte[sizeof(uint)];
        var headerBytes = await ReadAtMostAsync(_input, header, cancellationToken);
        if (headerBytes == 0)
        {
            return null;
        }

        if (headerBytes != header.Length)
        {
            throw new SidecarProtocolException(
                $"Truncated frame header: received {headerBytes} of {header.Length} bytes.");
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (payloadLength == 0)
        {
            throw new SidecarProtocolException("Zero-length JSON frames are not allowed.");
        }

        if (payloadLength > _maximumFrameBytes)
        {
            throw new SidecarProtocolException(
                $"Frame payload is {payloadLength} bytes; maximum is {_maximumFrameBytes} bytes.");
        }

        var payload = new byte[(int)payloadLength];
        var payloadBytes = await ReadAtMostAsync(_input, payload, cancellationToken);
        if (payloadBytes != payload.Length)
        {
            throw new SidecarProtocolException(
                $"Truncated frame payload: received {payloadBytes} of {payload.Length} bytes.");
        }

        try
        {
            return JsonDocument.Parse(payload, DocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new SidecarProtocolException("Frame payload is not valid UTF-8 JSON.", exception);
        }
    }

    public async ValueTask WriteFrameAsync<T>(
        T message,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(serializerOptions);

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(message, serializerOptions);
        }
        catch (NotSupportedException exception)
        {
            throw new SidecarProtocolException("Outgoing message cannot be serialized as JSON.", exception);
        }

        if (payload.Length == 0 || payload.Length > _maximumFrameBytes)
        {
            throw new SidecarProtocolException(
                $"Outgoing frame payload is {payload.Length} bytes; expected 1..{_maximumFrameBytes} bytes.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            // Cancellation is honored while waiting to become the next writer.
            // Once the header starts, the complete frame must be emitted; otherwise
            // a cancellation between header and payload permanently corrupts the
            // byte stream for every later message.
            await _output.WriteAsync(header, CancellationToken.None);
            await _output.WriteAsync(payload, CancellationToken.None);
            await _output.FlushAsync(CancellationToken.None);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeGate.Dispose();
    }

    private static async ValueTask<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
