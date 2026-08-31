using System.Text.Json;
using System.Text;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Append-only day-granularity record of usage that the readers already compute
/// and then discard.
///
/// Every statistic in the app is recomputed from source transcripts on each
/// refresh, so when those logs rotate or are cleaned the history goes with them —
/// permanently, including the equivalent-value figures the product is built
/// around. This store is the app's own memory of what it saw.
///
/// Writing is the irreversible half: a day never captured cannot be recovered
/// later. Reading back into the dashboard is deliberately separate, because a
/// restored day has to be reconciled against period totals that are still
/// recomputed live, and getting that wrong would make the chart disagree with
/// its own summary.
/// </summary>
public sealed class DailyUsageHistoryStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public DailyUsageHistoryStore(string? applicationDataDirectory = null)
    {
        var root = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _directory = Path.Combine(root, "history");
    }

    public string PathFor(AgentRuntime runtime) =>
        Path.Combine(_directory, $"daily-usage-{FileSegment(runtime)}-v{CurrentVersion}.jsonl");

    /// <summary>
    /// Backup restore deliberately preserves unknown future rows, but history files
    /// must remain text: accepting arbitrary binary data would leave the reader with
    /// a permanently poisoned managed file. Individual malformed/torn JSONL rows stay
    /// allowed because normal reads already skip them and they may be the only copy of
    /// surrounding valid history.
    /// </summary>
    internal static void ValidateBackupContent(ReadOnlySpan<byte> content)
    {
        try
        {
            var text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(content);
            if (text.Contains('\0', StringComparison.Ordinal))
            {
                throw new InvalidDataException("用量历史包含无效的 NUL 字符。");
            }
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("用量历史不是有效的 UTF-8 文本。", exception);
        }
    }

    /// <summary>
    /// Totals depend on which workspace and subagent filters were active when they
    /// were computed, so rows recorded under different settings are not comparable.
    /// The fingerprint travels with each row; rows from another scope are preserved
    /// but never mixed into a result.
    /// </summary>
    /// <summary>
    /// Identifies the slice of usage a recorded day belongs to.
    ///
    /// Only the workspace filter changes which usage is counted. An earlier version
    /// also folded in the show-subagents preference, which no writer's totals have
    /// ever depended on — it only governs the task board. Toggling that checkbox
    /// therefore forked the history into a second scope and made every previously
    /// recorded day unreadable, which for a store whose purpose is remembering days
    /// whose transcripts have since rotated away is the loss of the only surviving
    /// copy. <see cref="BelongsToScope"/> reclaims those rows.
    /// </summary>
    public static string ScopeFingerprint(string? defaultWorkspace) =>
        string.IsNullOrWhiteSpace(defaultWorkspace)
            ? "all"
            : Path.TrimEndingDirectorySeparator(defaultWorkspace.Trim()).ToLowerInvariant();

    /// <summary>
    /// Whether a recorded row belongs to the given scope, accepting the legacy
    /// fingerprint that carried a trailing show-subagents component. Both legacy
    /// variants map back onto the workspace they were always describing, so a user
    /// who toggled the checkbox gets their orphaned days back rather than merely
    /// stopping the bleeding.
    /// </summary>
    private static bool BelongsToScope(string? recordedScope, string scope) =>
        string.Equals(recordedScope, scope, StringComparison.Ordinal)
        || string.Equals(recordedScope, $"{scope}|subagents=0", StringComparison.Ordinal)
        || string.Equals(recordedScope, $"{scope}|subagents=1", StringComparison.Ordinal);

    /// <summary>Latest recorded day per date for the given scope, ascending.</summary>
    public async Task<IReadOnlyList<DailyUsageRecord>> LoadAsync(
        AgentRuntime runtime,
        string scope,
        CancellationToken cancellationToken = default)
    {
        var path = PathFor(runtime);
        if (!File.Exists(path))
        {
            return [];
        }

        var latest = new Dictionary<DateOnly, DailyUsageRecord>();
        try
        {
            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!TryParse(line, scope, out var record))
                {
                    continue;
                }

                // Last write for a date wins; the file is append-only within a run.
                latest[record.Date] = record;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return latest.Values.OrderBy(record => record.Date).ToArray();
    }

    /// <summary>
    /// Records the supplied days, replacing any earlier row for the same date and
    /// scope. Rows belonging to another scope are copied through untouched — a
    /// rewrite that dropped them would delete history the user can never regain
    /// simply because they changed a workspace filter.
    /// </summary>
    public async Task<string> SaveAsync(
        AgentRuntime runtime,
        IReadOnlyList<DailyUsageRecord> days,
        string scope,
        CancellationToken cancellationToken = default)
    {
        if (days.Count == 0)
        {
            return "用量历史：本次没有可记录的明细";
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(runtime);
            Directory.CreateDirectory(_directory);

            var replacing = days.Select(day => day.Date).ToHashSet();
            var carried = new List<string>();
            if (File.Exists(path))
            {
                foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    // Foreign scopes are opaque here and must survive verbatim. A legacy
                    // row for a date being rewritten is dropped rather than carried, or
                    // it would linger beside its replacement and grow the file forever.
                    if (!TryReadHeader(line, out var lineScope, out var lineDate)
                        || !BelongsToScope(lineScope, scope)
                        || !replacing.Contains(lineDate))
                    {
                        carried.Add(line);
                    }
                }
            }

            var written = days
                .OrderBy(day => day.Date)
                .Select(day => JsonSerializer.Serialize(ToRow(day, scope), JsonOptions));

            var temporary = path + ".tmp";
            await File.WriteAllLinesAsync(temporary, carried.Concat(written), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return $"用量历史：已记录 {days.Count} 天到 {path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing a history write is recoverable on the next refresh; failing the
            // refresh would take the whole dashboard down with it.
            return $"用量历史写入失败：{exception.Message}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryParse(string line, string scope, out DailyUsageRecord record)
    {
        record = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            var row = JsonSerializer.Deserialize<DailyUsageRow>(line, JsonOptions);
            if (row is null
                || row.V != CurrentVersion
                || !BelongsToScope(row.Scope, scope)
                || !DateOnly.TryParse(row.Date, out var date))
            {
                return false;
            }

            record = new DailyUsageRecord(
                date,
                row.Tokens?.ToBreakdown() ?? TokenBreakdown.Zero,
                row.CreditsUsed,
                row.UnratedTokens,
                Enum.TryParse<DataQuality>(row.Quality, ignoreCase: true, out var quality)
                    ? quality
                    : DataQuality.Approximate);
            return true;
        }
        catch (JsonException)
        {
            // A torn trailing line from a crash costs that day's last revision, not
            // the file.
            return false;
        }
    }

    private static bool TryReadHeader(string line, out string scope, out DateOnly date)
    {
        scope = string.Empty;
        date = default;
        try
        {
            var row = JsonSerializer.Deserialize<DailyUsageRow>(line, JsonOptions);
            if (row?.Scope is null || !DateOnly.TryParse(row.Date, out date))
            {
                return false;
            }

            scope = row.Scope;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DailyUsageRow ToRow(DailyUsageRecord day, string scope) => new(
        CurrentVersion,
        day.Date.ToString("yyyy-MM-dd"),
        scope,
        DateTimeOffset.UtcNow.ToString("O"),
        day.Quality.ToString(),
        TokenRow.From(day.Tokens),
        day.CreditsUsed,
        day.UnratedTokens);

    private static string FileSegment(AgentRuntime runtime) =>
        runtime == AgentRuntime.ClaudeCode ? "claude-code" : "codex";

    // Only constructor-backed values are stored. TokenBreakdown's computed
    // properties (UncachedInputTokens, BillableCacheWrite*, VisibleTotalTokens …)
    // serialize too, and persisting them would nearly double every line with
    // values that are pure functions of these seven.
    private sealed record TokenRow(
        long Input,
        long CachedInput,
        long Output,
        long ReasoningOutput,
        long Total,
        long CacheWrite5m,
        long CacheWrite1h)
    {
        public static TokenRow From(TokenBreakdown tokens) => new(
            tokens.InputTokens,
            tokens.CachedInputTokens,
            tokens.OutputTokens,
            tokens.ReasoningOutputTokens,
            tokens.TotalTokens,
            tokens.CacheWrite5mTokens,
            tokens.CacheWrite1hTokens);

        public TokenBreakdown ToBreakdown() => new(
            Input,
            CachedInput,
            Output,
            ReasoningOutput,
            Total,
            CacheWrite5m,
            CacheWrite1h);
    }

    private sealed record DailyUsageRow(
        int V,
        string Date,
        string Scope,
        string ObservedAt,
        string Quality,
        TokenRow? Tokens,
        double CreditsUsed,
        long UnratedTokens);
}

/// <summary>One day of usage as it was measured, independent of the source logs.</summary>
public sealed record DailyUsageRecord(
    DateOnly Date,
    TokenBreakdown Tokens,
    double CreditsUsed,
    long UnratedTokens,
    DataQuality Quality);
