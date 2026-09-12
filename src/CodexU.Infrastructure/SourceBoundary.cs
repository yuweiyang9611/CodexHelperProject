using System.Security.Cryptography;

namespace CodexU.Infrastructure;

internal static class SourceBoundary
{
    internal static async Task<string> ReadAsync(string path, long offset, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Validate the entire committed prefix: matching only its first/last blocks
        // would accept a rewritten middle followed by an append as a valid revision.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        for (long remaining = offset; remaining > 0;)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
