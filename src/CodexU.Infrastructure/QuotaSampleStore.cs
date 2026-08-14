using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Keeps a short trail of rate-limit readings so a burn rate can be measured.
///
/// A single reading only says how much of a window is gone, never how fast it is
/// going, and the app is a desktop process that gets closed and reopened — so the
/// trail has to survive on disk rather than in memory.
/// </summary>
public sealed class QuotaSampleStore
{
    private const int CurrentVersion = 1;
    private const int MaximumSamplesPerWindow = 120;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public QuotaSampleStore(string? applicationDataDirectory = null)
    {
        var root = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _path = Path.Combine(root, $"quota-samples-v{CurrentVersion}.json");
    }

    /// <summary>
    /// Readings are only comparable within one runtime and one window. Codex can
    /// report a window from either the app server or local rollouts, so the source
    /// belongs in the key too — a rate measured across a switch between them would
    /// be comparing two different measurements.
    /// </summary>
    public static string WindowKey(AgentRuntime runtime, string window, string source) =>
        $"{runtime}|{window}|{source}";

    /// <summary>
    /// Reads the trails, hands them to <paramref name="mutate"/>, and writes the result
    /// back — all while holding the gate. Two refreshes overlapping would otherwise each
    /// read the same trails, and whichever wrote last would discard the other's new
    /// reading.
    /// </summary>
    public async Task<T> UpdateAsync<T>(
        Func<IDictionary<string, IReadOnlyList<QuotaSample>>, T> mutate,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var trails = new Dictionary<string, IReadOnlyList<QuotaSample>>(
                await ReadAsync(cancellationToken).ConfigureAwait(false),
                StringComparer.Ordinal);
            var result = mutate(trails);
            await WriteAsync(trails, now, retention, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<QuotaSample>>> LoadAsync(
        CancellationToken cancellationToken = default) => ReadAsync(cancellationToken);

    /// <summary>Writes the trails, dropping anything outside the retention horizon.</summary>
    public async Task SaveAsync(
        IReadOnlyDictionary<string, IReadOnlyList<QuotaSample>> windows,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(windows, now, retention, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds a reading to a window's trail, ignoring one that repeats the newest
    /// figure at a later time — that is the same measurement seen twice, not
    /// evidence that consumption stalled.
    /// </summary>
    public static IReadOnlyList<QuotaSample> Append(
        IReadOnlyList<QuotaSample> existing,
        QuotaSample sample)
    {
        if (existing.Count > 0)
        {
            var newest = existing[^1];
            if (sample.ObservedAt <= newest.ObservedAt)
            {
                return existing;
            }

            if (Math.Abs(newest.UsedPercent - sample.UsedPercent) < double.Epsilon
                && newest.ResetsAt == sample.ResetsAt)
            {
                // Same reading, later clock. Recording it would stretch the measured
                // span without adding information and flatten the rate.
                return existing;
            }
        }

        return [.. existing, sample];
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<QuotaSample>>> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, IReadOnlyList<QuotaSample>>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = await JsonSerializer
                .DeserializeAsync<SampleDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (document?.Version != CurrentVersion || document.Windows is null)
            {
                return new Dictionary<string, IReadOnlyList<QuotaSample>>(StringComparer.Ordinal);
            }

            return document.Windows.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<QuotaSample>)(pair.Value ?? [])
                    .Select(row => new QuotaSample(row.ObservedAt, row.UsedPercent, row.ResetsAt))
                    .OrderBy(sample => sample.ObservedAt)
                    .ToArray(),
                StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, IReadOnlyList<QuotaSample>>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(
        IReadOnlyDictionary<string, IReadOnlyList<QuotaSample>> windows,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        try
        {
            var trimmed = windows.ToDictionary(
                pair => pair.Key,
                pair => pair.Value
                    .Where(sample => sample.ObservedAt >= now - retention && sample.ObservedAt <= now)
                    .OrderBy(sample => sample.ObservedAt)
                    .TakeLast(MaximumSamplesPerWindow)
                    .Select(sample => new SampleRow(sample.ObservedAt, sample.UsedPercent, sample.ResetsAt))
                    .ToArray(),
                StringComparer.Ordinal);

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer
                    .SerializeAsync(stream, new SampleDocument(CurrentVersion, trimmed), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A lost sample costs one interval of forecast accuracy; failing the
            // refresh would cost the whole dashboard.
        }
    }

    private sealed record SampleRow(DateTimeOffset ObservedAt, double UsedPercent, DateTimeOffset? ResetsAt);

    private sealed record SampleDocument(int Version, Dictionary<string, SampleRow[]>? Windows);
}
