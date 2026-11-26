using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>Reads model quota groups using Antigravity CLI's read-only print-mode /usage command.</summary>
public sealed class AntigravityQuotaReader
{
    private static readonly Version MinimumVersion = new(1, 1, 11);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] CooldownSchedule =
        [TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];
    private const int MaximumOutputBytes = 64 * 1024;
    private const int MaximumGroups = 32;
    private const int MaximumBucketsPerGroup = 16;

    private const int GuardVersion = 2;
    private const int MaximumGuardEntries = 8;
    private const int MaximumVersionLength = 64;
    private static readonly ConcurrentDictionary<string, AntigravityQuotaReader> ByDataDirectory =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string?> _discover;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ClaudeCliAuthCommandResult>> _run;
    private readonly TimeProvider _clock;
    private readonly string? _guardFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AntigravityQuotaSnapshot? _lastResult;
    private long _lastCompleted;
    // Oldest first, so a full guard can retain the most recent entries. Accessed only under _gate.
    private readonly List<string> _blockedVersions = [];
    private readonly List<VersionCooldown> _cooldowns = [];
    private bool _guardLoaded;

    internal AntigravityQuotaReader(
        Func<string?> discover,
        Func<ProcessStartInfo, CancellationToken, Task<ClaudeCliAuthCommandResult>> run,
        TimeProvider clock,
        string? guardFile = null)
    {
        _discover = discover;
        _run = run;
        _clock = clock;
        _guardFile = guardFile;
        _guardLoaded = guardFile is null;
    }

    /// <summary>The process-wide reader for one application data directory.</summary>
    public static AntigravityQuotaReader ForDataDirectory(string? applicationDataDirectory)
    {
        var directory = Path.GetFullPath(applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU"));
        return ByDataDirectory.GetOrAdd(directory, root => new(
            () => Discover(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("PATH")),
            ClaudeCliAuthStatusReader.RunProcessAsync,
            TimeProvider.System,
            Path.Combine(root, "antigravity", "quota-guard.json")));
    }

    public async Task<AntigravityQuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Not cached: the next refresh retries reading the guard.
            if (!LoadGuard()) return Empty(AntigravityQuotaStatus.Unavailable);
            if (_lastResult is not null && _clock.GetElapsedTime(_lastCompleted) < RefreshInterval)
            {
                return _lastResult;
            }

            _lastResult = await QueryAsync(cancellationToken);
            _lastCompleted = _clock.GetTimestamp();
            return _lastResult;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AntigravityQuotaSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        string? usageVersion = null;
        VersionCooldown? cooldown = null;
        try
        {
            var executable = _discover();
            if (executable is null) return Empty(AntigravityQuotaStatus.NotInstalled);

            var workingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codexU-antigravity-cli")).FullName;
            var version = await RunWithTimeoutAsync(StartInfo(executable, workingDirectory, ["--version"]),
                VersionTimeout, cancellationToken);
            if (version.ExitCode != 0) return Empty(AntigravityQuotaStatus.Unavailable);

            // Older versions may run unsupported slash commands as model prompts.
            var versionText = version.Output.Trim();
            if (versionText.Length > MaximumVersionLength || !Version.TryParse(versionText, out var parsedVersion)
                || parsedVersion < MinimumVersion)
                return Empty(AntigravityQuotaStatus.UnsupportedVersion);
            if (_blockedVersions.Contains(versionText))
                return Empty(AntigravityQuotaStatus.ModelInvoked);
            cooldown = _cooldowns.Find(entry => entry.CliVersion == versionText);
            if (cooldown is not null && cooldown.RetryAt > _clock.GetUtcNow())
                return Empty(AntigravityQuotaStatus.Unavailable);

            var info = StartInfo(executable, workingDirectory,
                ["-p", "/usage", "--output-format", "json", "--print-timeout", "12s"]);
            cancellationToken.ThrowIfCancellationRequested();
            usageVersion = versionText;
            // Give print mode its full timeout plus cleanup time, even after a slow version check.
            var output = await RunWithTimeoutAsync(info, UsageTimeout, cancellationToken);
            var result = Parse(output, _clock.GetUtcNow(), out var verifiedZeroTurns);
            if (result.Status == AntigravityQuotaStatus.ModelInvoked)
            {
                AddGuardEntry(_blockedVersions, versionText);
                if (cooldown is not null) _cooldowns.Remove(cooldown);
                SaveGuard();
            }
            else if (!verifiedZeroTurns)
            {
                RecordInconclusive(versionText, cooldown);
            }
            else if (cooldown is not null)
            {
                _cooldowns.Remove(cooldown);
                SaveGuard();
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException
            or OperationCanceledException or System.Security.SecurityException)
        {
            if (usageVersion is not null) RecordInconclusive(usageVersion, cooldown);
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            // Keep local executable paths and CLI diagnostics out of the renderer.
            return Empty(AntigravityQuotaStatus.Unavailable);
        }
    }

    private async Task<ClaudeCliAuthCommandResult> RunWithTimeoutAsync(
        ProcessStartInfo info, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        return await _run(info, linked.Token);
    }

    private void RecordInconclusive(string version, VersionCooldown? previous)
    {
        var failures = Math.Min((previous?.FailureCount ?? 0) + 1, CooldownSchedule.Length);
        if (previous is not null) _cooldowns.Remove(previous);
        AddGuardEntry(_cooldowns, new VersionCooldown(version, failures, _clock.GetUtcNow() + CooldownSchedule[failures - 1]));
        SaveGuard();
    }

    private static void AddGuardEntry<T>(List<T> entries, T entry)
    {
        if (entries.Count == MaximumGuardEntries) entries.RemoveAt(0);
        entries.Add(entry);
    }

    internal static string? Discover(string localApplicationData, string? searchPath)
    {
        var fileName = OperatingSystem.IsWindows() ? "agy.exe" : "agy";
        if (Path.IsPathFullyQualified(localApplicationData))
        {
            var installed = Path.Combine(localApplicationData, "agy", "bin", fileName);
            if (File.Exists(installed)) return installed;
        }
        foreach (var entry in (searchPath ?? string.Empty).Split(Path.PathSeparator).Take(256))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)
                || directory.StartsWith("//", StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static ProcessStartInfo StartInfo(string executable, string workingDirectory, string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static AntigravityQuotaSnapshot Parse(ClaudeCliAuthCommandResult result, DateTimeOffset observedAt) =>
        Parse(result, observedAt, out _);

    private static AntigravityQuotaSnapshot Parse(
        ClaudeCliAuthCommandResult result, DateTimeOffset observedAt, out bool verifiedZeroTurns)
    {
        verifiedZeroTurns = false;
        if (Encoding.UTF8.GetByteCount(result.Output) > MaximumOutputBytes)
            return Empty(AntigravityQuotaStatus.Unavailable);

        try
        {
            using var json = JsonDocument.Parse(result.Output, new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowDuplicateProperties = false
            });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty(AntigravityQuotaStatus.Unavailable);
            // Checked before the exit code: a /usage that fell through to a model turn
            // ends with status ERROR and a non-zero exit, yet has already spent quota.
            if (InvokedModel(root)) return Empty(AntigravityQuotaStatus.ModelInvoked);
            verifiedZeroTurns = Number(root, "num_turns", 0) == 0;
            if (!verifiedZeroTurns || result.ExitCode != 0 || Text(root, "status") != "SUCCESS"
                || !Object(root, "command", out var command) || Text(command, "name") != "usage"
                || !Object(command, "data", out var data) || !Array(data, "groups", MaximumGroups, out var groups))
                return Empty(AntigravityQuotaStatus.Unavailable);

            var parsed = new List<AntigravityQuotaGroup>();
            foreach (var group in groups.EnumerateArray())
            {
                // One malformed or unexpected group (for example a credits-only group
                // without buckets) must not hide every other group's quota.
                if (!Array(group, "buckets", MaximumBucketsPerGroup, out var buckets)) continue;
                if (SafeLabel(Text(group, "name")) is not { } name) continue;
                var rows = new List<AntigravityQuotaBucket>();
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (ParseBucket(bucket) is { } row) rows.Add(row);
                }
                if (rows.Count > 0) parsed.Add(new(name, rows.AsReadOnly()));
            }
            return new(parsed.Count > 0 ? AntigravityQuotaStatus.Ready : AntigravityQuotaStatus.NoQuota,
                parsed.AsReadOnly(), observedAt);
        }
        catch (JsonException)
        {
            return Empty(AntigravityQuotaStatus.Unavailable);
        }
    }

    private static AntigravityQuotaBucket? ParseBucket(JsonElement bucket)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return null;
        var label = Text(bucket, "window") switch
        {
            "weekly" => "每周",
            "5h" => "5 小时",
            _ => SafeLabel(Text(bucket, "name"))
        };
        if (label is null) return null;
        if (bucket.TryGetProperty("disabled", out var disabled)
            && disabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) return null;

        var fraction = Number(bucket, "remaining_fraction", 1);
        var amount = Number(bucket, "remaining_amount", double.MaxValue);
        var reset = ResetTime(bucket);
        var isDisabled = disabled.ValueKind == JsonValueKind.True;
        if (fraction is null && amount is null && reset is null && !isDisabled) return null;
        return new(label, fraction * 100, amount, reset, isDisabled);
    }

    private static DateTimeOffset? ResetTime(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("reset_time", out var value) || value.ValueKind != JsonValueKind.String) return null;
        var time = value.GetString();
        if (time is not { Length: >= 20 and <= 48 } || time[10] != 'T'
            || !(time.EndsWith('Z') || time[^6] is '+' or '-')) return null;
        return value.TryGetDateTimeOffset(out var parsed) ? parsed : null;
    }

    private static AntigravityQuotaSnapshot Empty(AntigravityQuotaStatus status) => new(status, []);

    private sealed record QuotaGuard(int Version, string[] BlockedCliVersions, VersionCooldown[] Cooldowns);

    private sealed record LegacyQuotaGuard(string? ModelInvokingCliVersion);

    private sealed record VersionCooldown(string CliVersion, int FailureCount, DateTimeOffset RetryAt);

    private static bool IsGuardVersion(string? version) =>
        version is { Length: <= MaximumVersionLength } && Version.TryParse(version, out _);

    /// <summary>
    /// Returns false when a guard file exists but cannot be read right now (for example
    /// while another program holds a lock on it). Querying then could re-run a CLI
    /// version the file blocks, so the caller skips the query and loading retries later.
    /// </summary>
    private bool LoadGuard()
    {
        if (_guardLoaded) return true;
        try
        {
            var file = new FileInfo(_guardFile!);
            if (file.Length <= 4096)
            {
                var json = File.ReadAllText(file.FullName);
                var guard = JsonSerializer.Deserialize<QuotaGuard>(json, GuardJson);
                if (guard is { Version: GuardVersion })
                {
                    _blockedVersions.AddRange((guard.BlockedCliVersions ?? []).Where(IsGuardVersion)
                        .Distinct(StringComparer.Ordinal).TakeLast(MaximumGuardEntries));
                    var latestRetry = _clock.GetUtcNow() + CooldownSchedule[^1];
                    _cooldowns.AddRange((guard.Cooldowns ?? [])
                        .Where(entry => entry is not null && IsGuardVersion(entry.CliVersion)
                            && entry.FailureCount >= 1 && entry.FailureCount <= CooldownSchedule.Length
                            && !_blockedVersions.Contains(entry.CliVersion))
                        .DistinctBy(entry => entry.CliVersion, StringComparer.Ordinal).TakeLast(MaximumGuardEntries)
                        .Select(entry => entry.RetryAt > latestRetry ? entry with { RetryAt = latestRetry } : entry));
                }
                else if (guard is { Version: 1 }
                    && JsonSerializer.Deserialize<LegacyQuotaGuard>(json, GuardJson) is { ModelInvokingCliVersion: { } saved }
                    && IsGuardVersion(saved))
                {
                    _blockedVersions.Add(saved);
                    SaveGuard();
                }
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException
            or JsonException or NotSupportedException or ArgumentException)
        {
            // Missing or unusable: nothing is blocked.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return false;
        }
        _guardLoaded = true;
        return true;
    }

    private void SaveGuard()
    {
        if (_guardFile is null) return;
        var temporary = $"{_guardFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_guardFile)!);
            var guard = new QuotaGuard(GuardVersion, [.. _blockedVersions], [.. _cooldowns]);
            File.WriteAllText(temporary, JsonSerializer.Serialize(guard, GuardJson));
            File.Move(temporary, _guardFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Best effort: the in-memory guard still protects this process.
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly JsonSerializerOptions GuardJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// True when the print-mode run reports any model turn or token use. A genuine
    /// /usage answer has zero turns; field names under usage vary, so any positive
    /// token counter counts.
    /// </summary>
    private static bool InvokedModel(JsonElement root)
    {
        if (root.TryGetProperty("num_turns", out var turns) && turns.ValueKind == JsonValueKind.Number
            && turns.TryGetDouble(out var count) && count > 0) return true;
        if (!Object(root, "usage", out var usage)) return false;
        return usage.EnumerateObject().Any(property => property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)
            && property.Value.ValueKind == JsonValueKind.Number
            && property.Value.TryGetDouble(out var tokens) && tokens > 0);
    }

    private static bool Object(JsonElement parent, string key, out JsonElement value) =>
        parent.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool Array(JsonElement parent, string key, int limit, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out value)
            && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= limit;
    }

    private static string? Text(JsonElement parent, string key) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? Number(JsonElement parent, string key, double maximum) =>
        parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0 && number <= maximum ? number : null;

    private static string? SafeLabel(string? label)
    {
        var trimmed = label?.Trim();
        return trimmed is { Length: > 0 and <= 100 }
            && !trimmed.StartsWith('/')
            && !trimmed.Contains(":/", StringComparison.Ordinal)
            && trimmed.All(character => !char.IsControl(character) && character is not ('@' or '\\')) ? trimmed : null;
    }
}
