using System.Text.Json;

namespace CodexU.Infrastructure;

internal sealed class SessionIndexCache
{
    // Version 9 persists compact normalized token events plus session/fork identity
    // under the field-validating, last-only-aware normalizer semantics. Global
    // canonicalization and parent-prefix removal are recomputed on every read.
    private const int CurrentVersion = 9;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public SessionIndexCache(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _path = Path.Combine(directory, "session-index-v1.json");
    }

    public async Task<Dictionary<string, SessionIndexEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = await JsonSerializer.DeserializeAsync<SessionIndexDocument>(stream, JsonOptions, cancellationToken);
            if (document?.Version != CurrentVersion
                || !string.Equals(document.TimeZoneId, TimeZoneInfo.Local.Id, StringComparison.Ordinal)
                || document.Entries is null)
            {
                return new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var entries = new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Entries)
            {
                if (IsUsable(entry))
                {
                    // A partially merged or manually repaired cache can contain duplicate
                    // paths. The newest occurrence is sufficient and must not take the
                    // entire local usage reader down.
                    entries[entry.Path] = entry;
                }
            }

            return entries;
        }
        catch (JsonException)
        {
            return new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException)
        {
            return new Dictionary<string, SessionIndexEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsUsable(SessionIndexEntry? entry) =>
        entry is not null
        && !string.IsNullOrWhiteSpace(entry.Path)
        && entry.Length >= 0
        && entry.Parsed is not null
        && entry.Parsed.Offset >= 0
        && entry.Parsed.Offset == entry.Length
        && entry.Parsed.UsageBuckets is not null
        && entry.Parsed.UsageBuckets.All(bucket => bucket is not null
                                                  && bucket.Tokens is not null
                                                  && bucket.Model is not null
                                                  && bucket.EventCount >= 0)
        && entry.Parsed.Tools is not null
        && entry.Parsed.Skills is not null
        && entry.Parsed.TaskLifecycle is not null
        && entry.Parsed.TokenCounterState is not null
        && entry.Parsed.TokenCounterState.LastCumulative is not null
        && entry.Parsed.TokenCounterState.HighWater is not null
        && entry.Parsed.CurrentModel is not null
        && Enum.IsDefined(entry.Parsed.ForkReplayPhase)
        && entry.Parsed.TokenEvents is not null
        && entry.Parsed.TokenEvents.All(tokenEvent => tokenEvent is not null
                                                     && tokenEvent.Model is not null
                                                     && tokenEvent.Tokens is not null
                                                     && tokenEvent.Identity is not null)
        && entry.Parsed.SkippedLineCount >= 0;

    public async Task SaveAsync(
        IEnumerable<SessionIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new SessionIndexDocument(CurrentVersion, TimeZoneInfo.Local.Id, entries.ToArray()),
                    JsonOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (IOException)
        {
            // Cache failure must not make the dashboard unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // Cache failure must not make the dashboard unavailable.
        }
    }
}

internal sealed record SessionIndexDocument(
    int Version,
    string? TimeZoneId,
    IReadOnlyList<SessionIndexEntry>? Entries);

internal sealed record SessionIndexEntry(
    string Path,
    long Length,
    long LastWriteTimeUtcTicks,
    ParsedSessionFile Parsed)
{
    public bool Matches(FileInfo file) =>
        Length == file.Length && LastWriteTimeUtcTicks == file.LastWriteTimeUtc.Ticks;
}
