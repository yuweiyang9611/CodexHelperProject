using System.Buffers;
using System.Text;

namespace CodexU.Infrastructure;

/// <summary>
/// Reads UTF-8, newline-delimited data without ever materializing more than the
/// configured number of bytes for one line. Oversized lines are drained through
/// their newline so the caller can continue with the next record.
/// </summary>
internal sealed class BoundedLineReader
{
    public const int DefaultMaximumLineBytes = 4 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly int _maximumLineBytes;
    private readonly byte[] _readBuffer;
    private int _bufferOffset;
    private int _bufferLength;
    private bool _isFirstLine = true;

    public BoundedLineReader(
        Stream stream,
        int maximumLineBytes = DefaultMaximumLineBytes,
        int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        _stream = stream;
        _maximumLineBytes = maximumLineBytes;
        _readBuffer = new byte[bufferSize];
    }

    public async ValueTask<BoundedLineReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var bytes = new ArrayBufferWriter<byte>(Math.Min(4096, _maximumLineBytes));
        var isTooLong = false;
        var hasBytes = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_bufferOffset >= _bufferLength)
            {
                _bufferLength = await _stream.ReadAsync(_readBuffer, cancellationToken);
                _bufferOffset = 0;
                if (_bufferLength == 0)
                {
                    return hasBytes
                        ? CreateResult(bytes.WrittenSpan, isTooLong)
                        : BoundedLineReadResult.EndOfStream;
                }
            }

            var available = _readBuffer.AsSpan(_bufferOffset, _bufferLength - _bufferOffset);
            var newlineIndex = available.IndexOf((byte)'\n');
            var segmentLength = newlineIndex >= 0 ? newlineIndex : available.Length;
            var segment = available[..segmentLength];
            hasBytes |= segmentLength > 0;

            if (!isTooLong)
            {
                if (bytes.WrittenCount > _maximumLineBytes - segmentLength)
                {
                    isTooLong = true;
                }
                else
                {
                    bytes.Write(segment);
                }
            }

            _bufferOffset += segmentLength;
            if (newlineIndex < 0)
            {
                continue;
            }

            _bufferOffset++;
            return CreateResult(bytes.WrittenSpan, isTooLong);
        }
    }

    private BoundedLineReadResult CreateResult(ReadOnlySpan<byte> bytes, bool isTooLong)
    {
        if (isTooLong)
        {
            _isFirstLine = false;
            return new BoundedLineReadResult(null, true, false);
        }

        if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }

        if (_isFirstLine
            && bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        _isFirstLine = false;
        return new BoundedLineReadResult(Encoding.UTF8.GetString(bytes), false, false);
    }
}

internal readonly record struct BoundedLineReadResult(string? Line, bool IsTooLong, bool IsEndOfStream)
{
    public static BoundedLineReadResult EndOfStream { get; } = new(null, false, true);
}
