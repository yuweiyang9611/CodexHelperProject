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
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);
    private const int MaximumOutputBytes = 64 * 1024;
    private const int MaximumGroups = 32;
    private const int MaximumBucketsPerGroup = 16;

    public static AntigravityQuotaReader Shared { get; } = new(
        () => Discover(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH")),
        ClaudeCliAuthStatusReader.RunProcessAsync,
        TimeProvider.System);

    private readonly Func<string?> _discover;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ClaudeCliAuthCommandResult>> _run;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AntigravityQuotaSnapshot? _lastResult;
    private long _lastCompleted;

    internal AntigravityQuotaReader(
        Func<string?> discover,
        Func<ProcessStartInfo, CancellationToken, Task<ClaudeCliAuthCommandResult>> run,
        TimeProvider clock)
    {
        _discover = discover;
        _run = run;
        _clock = clock;
    }

    public async Task<AntigravityQuotaSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(QueryTimeout);
        try
        {
            var executable = _discover();
            if (executable is null) return Empty(AntigravityQuotaStatus.NotInstalled);

            var workingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codexU-antigravity-cli")).FullName;
            var version = await _run(StartInfo(executable, workingDirectory, ["--version"]), deadline.Token);
            if (version.ExitCode != 0) return Empty(AntigravityQuotaStatus.Unavailable);

            // Older versions may run unsupported slash commands as model prompts.
            if (!Version.TryParse(version.Output.Trim(), out var parsedVersion) || parsedVersion < MinimumVersion)
                return Empty(AntigravityQuotaStatus.UnsupportedVersion);

            var info = StartInfo(executable, workingDirectory,
                ["-p", "/usage", "--output-format", "json", "--print-timeout", "12s"]);
            var result = await _run(info, deadline.Token);
            return Parse(result, _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException
            or OperationCanceledException or System.Security.SecurityException)
        {
            // Keep local executable paths and CLI diagnostics out of the renderer.
            return Empty(AntigravityQuotaStatus.Unavailable);
        }
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

    internal static AntigravityQuotaSnapshot Parse(ClaudeCliAuthCommandResult result, DateTimeOffset observedAt)
    {
        if (result.ExitCode != 0 || Encoding.UTF8.GetByteCount(result.Output) > MaximumOutputBytes)
            return Empty(AntigravityQuotaStatus.Unavailable);

        try
        {
            using var json = JsonDocument.Parse(result.Output, new JsonDocumentOptions
            {
                MaxDepth = 16,
                AllowDuplicateProperties = false
            });
            var root = json.RootElement;
            if (Text(root, "status") != "SUCCESS" || Number(root, "num_turns", 0) != 0
                || !Object(root, "command", out var command) || Text(command, "name") != "usage"
                || !Object(command, "data", out var data) || !Array(data, "groups", MaximumGroups, out var groups))
                return Empty(AntigravityQuotaStatus.Unavailable);

            var parsed = new List<AntigravityQuotaGroup>();
            foreach (var group in groups.EnumerateArray())
            {
                if (!Array(group, "buckets", MaximumBucketsPerGroup, out var buckets))
                    return Empty(AntigravityQuotaStatus.Unavailable);
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
