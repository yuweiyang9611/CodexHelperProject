using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Persists identities for quota windows whose source omits <see cref="RateLimitWindow.ResetsAt"/>.
/// The last remaining percentage is part of the state: a sizeable rise marks a
/// rollover and allocates a fresh identity even when both windows fall in the same
/// duration-sized UTC bucket.
/// </summary>
public sealed class PersistentQuotaWindowIdentityProvider : IQuotaWindowIdentityProvider
{
    private const int CurrentVersion = 1;
    private const int MaximumWindows = 32;
    private const int MaximumStateBytes = 64 * 1024;
    private const int MaximumLabelLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, WindowState> _windows;

    public PersistentQuotaWindowIdentityProvider(string? applicationDataDirectory = null)
    {
        var directory = Path.GetFullPath(applicationDataDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "codexU"));
        _path = Path.Combine(directory, $"quota-notification-windows-v{CurrentVersion}.json");
        _windows = ReadState();
    }

    public string StatePath => _path;

    public string ObserveWindow(
        AgentRuntime runtime,
        string label,
        RateLimitWindow quota,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(quota);
        if (string.IsNullOrWhiteSpace(label) || label.Length > MaximumLabelLength)
        {
            throw new ArgumentException("Quota window labels must be between 1 and 64 characters.", nameof(label));
        }

        if (quota.ResetsAt is { } reset)
        {
            return $"{runtime}|{label}|reset:{reset.UtcDateTime.Ticks}";
        }

        lock (_gate)
        {
            var stateKey = $"{runtime}|{label}";
            var remaining = quota.RemainingPercent;
            var hasPrevious = _windows.TryGetValue(stateKey, out var previous);
            var rolledOver = hasPrevious
                && remaining > previous!.LastRemainingPercent
                    + QuotaAlertState.WindowRolloverRisePercent;
            var identity = hasPrevious && !rolledOver
                ? previous!.Identity
                : Guid.NewGuid().ToString("N");

            if (!hasPrevious && _windows.Count >= MaximumWindows)
            {
                var oldest = _windows
                    .OrderBy(pair => pair.Value.LastObservedAt)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .First();
                _windows.Remove(oldest.Key);
            }

            var changed = !hasPrevious
                || rolledOver
                || Math.Abs(previous!.LastRemainingPercent - remaining) > double.Epsilon;
            _windows[stateKey] = new WindowState(identity, remaining, observedAt);
            if (changed)
            {
                TryWriteState();
            }

            return $"{stateKey}|window:{identity}";
        }
    }

    private Dictionary<string, WindowState> ReadState()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, WindowState>(StringComparer.Ordinal);
            }

            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var payload = new byte[MaximumStateBytes + 1];
            var length = 0;
            while (length < payload.Length)
            {
                var read = stream.Read(payload, length, payload.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length is <= 0 or > MaximumStateBytes)
            {
                return new Dictionary<string, WindowState>(StringComparer.Ordinal);
            }

            var document = JsonSerializer.Deserialize<StateDocument>(
                payload.AsSpan(0, length),
                JsonOptions);
            if (document?.Version != CurrentVersion || document.Windows is null)
            {
                return new Dictionary<string, WindowState>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, WindowState>(StringComparer.Ordinal);
            foreach (var entry in document.Windows
                         .OfType<StateEntry>()
                         .Where(IsValid)
                         .OrderByDescending(entry => entry.LastObservedAt)
                         .ThenBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (result.Count >= MaximumWindows)
                {
                    break;
                }

                result.TryAdd(
                    entry.Key,
                    new WindowState(
                        entry.Identity,
                        entry.LastRemainingPercent,
                        entry.LastObservedAt));
            }

            return result;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            // Notification de-duplication must never prevent the dashboard from
            // starting. A later observation replaces unreadable state atomically.
            return new Dictionary<string, WindowState>(StringComparer.Ordinal);
        }
    }

    private void TryWriteState()
    {
        var temporaryPath = _path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var entries = _windows
                .OrderByDescending(pair => pair.Value.LastObservedAt)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(MaximumWindows)
                .Select(pair => new StateEntry(
                    pair.Key,
                    pair.Value.Identity,
                    pair.Value.LastRemainingPercent,
                    pair.Value.LastObservedAt))
                .ToArray();
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    new StateDocument(CurrentVersion, entries),
                    JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (new FileInfo(temporaryPath).Length > MaximumStateBytes)
            {
                throw new InvalidDataException("Quota notification state exceeded its size limit.");
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or NotSupportedException)
        {
            // The in-memory identity remains valid for this process. Persistence is
            // best effort so an unavailable profile directory cannot break refresh.
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unique temporary names keep a failed cleanup from blocking a retry.
            }
        }
    }

    private static bool IsValid(StateEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.Key)
        && entry.Key.Length <= MaximumLabelLength + 32
        && Guid.TryParseExact(entry.Identity, "N", out _)
        && double.IsFinite(entry.LastRemainingPercent)
        && entry.LastRemainingPercent is >= 0 and <= 100;

    private sealed record WindowState(
        string Identity,
        double LastRemainingPercent,
        DateTimeOffset LastObservedAt);

    private sealed record StateEntry(
        string Key,
        string Identity,
        double LastRemainingPercent,
        DateTimeOffset LastObservedAt);

    private sealed record StateDocument(
        int Version,
        IReadOnlyList<StateEntry?>? Windows);
}
