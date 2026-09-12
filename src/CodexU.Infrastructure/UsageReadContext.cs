using System.Collections.Concurrent;

namespace CodexU.Infrastructure;

/// <summary>Process-local, reconstructible state. The application releases its root on shutdown.</summary>
public sealed class UsageReadContext
{
    private static readonly ConcurrentDictionary<string, UsageReadContext> Roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);
    internal long LedgerDeserialized;
    internal long LedgerWrites;
    internal long SourceComparisons;
    internal sealed class LedgerMemory
    {
        internal string? Stamp;
        internal readonly Dictionary<string, object> Runtimes = new(StringComparer.Ordinal);
    }
    internal static string Root(string? root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU")));
    internal static UsageReadContext For(string? root) => Roots.GetOrAdd(Root(root), _ => new());
    public static void Invalidate(string? root) => Roots.TryRemove(Root(root), out _);
    internal T Get<T>(string key, Func<T> create) where T : class => (T)_values.GetOrAdd(key, _ => create());
    internal static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{SourceFileIdentity.Read(path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A failed cache inspection must force a miss, not prevent the source fallback.
            return "unreadable:" + Guid.NewGuid().ToString("N");
        }
    }

    internal sealed class FileCache<T>
    {
        internal string? Stamp;
        internal T? Value;
    }

    internal sealed class ValidationSchedule
    {
        internal DateTimeOffset? LastSuccess;
        internal bool Due(DateTimeOffset now) => LastSuccess is null || now < LastSuccess || now - LastSuccess >= TimeSpan.FromHours(1);
    }
}
