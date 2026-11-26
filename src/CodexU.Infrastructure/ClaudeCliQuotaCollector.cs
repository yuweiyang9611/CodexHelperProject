using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Reads the plan windows of the official Claude CLI's own login by sending one
/// get_usage control frame to an unmodified CLI process. No user message, prompt or
/// model request is ever written, and no credential or account field is read.
/// A standalone CLI process has no usage cache, so every query reaches Anthropic's
/// usage endpoint, which is rate limited per account and also polled by Claude
/// Desktop. One instance per data directory therefore shares results: callers within
/// <see cref="MinimumInterval"/> reuse the last attempt, concurrent callers share one
/// process, failures back off, and that schedule is saved so restarting the app
/// (updates, crash recovery, development runs) cannot bypass it.
/// </summary>
public sealed class ClaudeCliQuotaCollector
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    // The response does not say whether the endpoint rate-limited the CLI (it then
    // returns no windows or an unlisted seed), so every failure backs off as if it did.
    internal static readonly TimeSpan[] FailureBackoff =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30)];
    internal static readonly TimeSpan NotAvailableRetry = TimeSpan.FromMinutes(60);
    internal static readonly string[] Arguments =
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        "--strict-mcp-config", "--no-session-persistence", "--safe-mode"
    ];
    private const int MaximumStdoutLines = 64;
    private const int StateVersion = 1;
    private const long MaximumStateBytes = 16 * 1024;
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, ClaudeCliQuotaCollector> ByDataDirectory =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web);

    private readonly Func<ClaudeCliExecutable?> _discover;
    private readonly Func<ClaudeCliExecutable, string, CancellationToken, Task<string>> _query;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly string? _stateFile;
    private readonly Lock _gate = new();
    private Task<ClaudeCliQuotaReading>? _inFlight;
    private ClaudeCliQuotaReading? _lastAttempt;
    private ClaudeCliQuotaReading? _lastSuccess;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _failures;
    private DateTimeOffset? _lastAttemptAt;
    private bool _stateLoaded;

    internal ClaudeCliQuotaCollector(Func<ClaudeCliExecutable?> discover,
        Func<ClaudeCliExecutable, string, CancellationToken, Task<string>> query, TimeProvider clock, TimeSpan timeout,
        string? stateFile = null)
    {
        _discover = discover;
        _query = query;
        _clock = clock;
        _timeout = timeout;
        _stateFile = stateFile;
        _stateLoaded = stateFile is null;
    }

    /// <summary>
    /// The process-wide collector for one application data directory, whose query
    /// schedule is kept in that directory across restarts.
    /// </summary>
    public static ClaudeCliQuotaCollector ForDataDirectory(string? applicationDataDirectory)
    {
        var directory = Path.GetFullPath(applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU"));
        return ByDataDirectory.GetOrAdd(directory, root => new(
            () => ClaudeCliAuthStatusReader.Discover(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("PATH")),
            (executable, requestId, token) => RunProcessAsync(StartInfo(executable.Path, WorkingDirectory()), requestId, token),
            TimeProvider.System, DefaultTimeout, Path.Combine(root, "claude-code", "cli-quota-state.json")));
    }

    /// <summary>Applies <see cref="RetrySoon"/> to every collector created in this process.</summary>
    public static void RetrySoonAfterLogin()
    {
        foreach (var collector in ByDataDirectory.Values) collector.RetrySoon();
    }

    internal async Task<ClaudeCliQuotaReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ClaudeCliQuotaReading>? attempt;
        lock (_gate)
        {
            EnsureStateLoaded();
            // The attempt runs on the thread pool so it can never re-enter this lock
            // on the caller's thread, and with its own deadline so one cancelled
            // refresh does not abort a query another refresh is waiting for.
            if (_inFlight is null && _clock.GetUtcNow() >= _nextAttemptAt) _inFlight = Task.Run(AttemptAsync);
            attempt = _inFlight;
        }
        if (attempt is not null) await attempt.WaitAsync(cancellationToken);
        lock (_gate) return Current(_clock.GetUtcNow());
    }

    /// <summary>
    /// Called when the CLI reports a login. Only the long wait after "not signed in"
    /// (or a missing CLI) is cleared so the next refresh queries at once. A failure
    /// backoff stays: it may be the usage endpoint's rate limit, and the settings page
    /// checks the login every time it opens.
    /// </summary>
    public void RetrySoon()
    {
        lock (_gate)
        {
            EnsureStateLoaded();
            if (_lastAttempt?.Status is not (ClaudeCliQuotaStatus.NotAvailable or ClaudeCliQuotaStatus.NotInstalled)) return;
            _failures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
            SaveState();
        }
    }

    /// <summary>Describes the schedule for display; never starts a query.</summary>
    public ClaudeCliQuotaSchedule Describe(bool enabled)
    {
        lock (_gate)
        {
            EnsureStateLoaded();
            return new(enabled, _lastAttempt?.Status, _lastAttemptAt, _lastSuccess?.ObservedAt,
                _nextAttemptAt > _clock.GetUtcNow() ? _nextAttemptAt : null);
        }
    }

    private ClaudeCliQuotaReading Current(DateTimeOffset now)
    {
        var attempt = _lastAttempt ?? ClaudeCliQuotaParser.Unavailable(null, "尚未完成查询");
        if (attempt.Success) return WithoutExpiredWindows(attempt, now);
        // A transient failure keeps the last good reading visible while it is still
        // recent; its windows carry their own read time, so it is not shown as new.
        // A missing CLI or a login without plan windows means that reading no longer
        // describes the CLI's account, so it is not reused.
        if (attempt.Status == ClaudeCliQuotaStatus.Unavailable && _lastSuccess is { ObservedAt: { } readAt } success
            && readAt <= now && now - readAt <= MaximumAge)
        {
            var kept = WithoutExpiredWindows(success, now);
            return kept with { Diagnostic = $"{attempt.Diagnostic}；继续使用 {readAt:O} 读取的结果" };
        }
        return attempt;
    }

    private static ClaudeCliQuotaReading WithoutExpiredWindows(ClaudeCliQuotaReading reading, DateTimeOffset now)
    {
        var primary = reading.Primary?.ResetsAt is { } primaryReset && primaryReset <= now ? null : reading.Primary;
        var secondary = reading.Secondary?.ResetsAt is { } secondaryReset && secondaryReset <= now ? null : reading.Secondary;
        if (primary == reading.Primary && secondary == reading.Secondary) return reading;
        return primary is null && secondary is null
            ? reading with
            {
                Status = ClaudeCliQuotaStatus.NoWindows,
                Primary = null,
                Secondary = null,
                Diagnostic = "Claude CLI 后台额度：最近一次结果没有仍有效的一般额度窗口"
            }
            : reading with { Primary = primary, Secondary = secondary };
    }

    private async Task<ClaudeCliQuotaReading> AttemptAsync()
    {
        ClaudeCliQuotaReading reading;
        try
        {
            var executable = _discover();
            if (executable is null)
            {
                reading = new(ClaudeCliQuotaStatus.NotInstalled, null, null, null, "Claude CLI 后台额度：未找到 Claude Code CLI");
            }
            else
            {
                var requestId = "codexu-quota-" + Guid.NewGuid().ToString("N");
                using var deadline = new CancellationTokenSource(_timeout, _clock);
                var line = await _query(executable, requestId, deadline.Token);
                reading = ClaudeCliQuotaParser.Parse(line, requestId, _clock.GetUtcNow());
            }
        }
        catch (OperationCanceledException)
        {
            reading = ClaudeCliQuotaParser.Unavailable(null, "查询超时");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException or NotSupportedException or ArgumentException
            or JsonException or System.Security.SecurityException)
        {
            // Never surface the exception text: it can carry local paths.
            reading = ClaudeCliQuotaParser.Unavailable(null, "无法完成查询");
        }

        lock (_gate)
        {
            var completedAt = _clock.GetUtcNow();
            _lastAttempt = reading;
            _lastAttemptAt = completedAt;
            if (reading.Success)
            {
                _lastSuccess = reading;
                _failures = 0;
                _nextAttemptAt = completedAt + MinimumInterval;
            }
            else if (reading.Status == ClaudeCliQuotaStatus.NotInstalled)
            {
                // Discovery is only a few file checks; recheck at the normal pace.
                _nextAttemptAt = completedAt + MinimumInterval;
            }
            else if (reading.Status == ClaudeCliQuotaStatus.NotAvailable)
            {
                // Most Desktop users have an embedded CLI they never signed in to.
                // That state is stable, so do not start the CLI for it every few
                // minutes; a detected login clears the wait (see RetrySoon).
                _failures = 0;
                _nextAttemptAt = completedAt + NotAvailableRetry;
            }
            else
            {
                _failures++;
                _nextAttemptAt = completedAt + FailureBackoff[Math.Min(_failures, FailureBackoff.Length) - 1];
            }
            _inFlight = null;
            SaveState();
        }
        return reading;
    }

    private sealed record PersistedWindow(double UsedPercent, DateTimeOffset? ResetsAt);

    private sealed record PersistedReading(DateTimeOffset ObservedAt, PersistedWindow? Primary, PersistedWindow? Secondary);

    private sealed record PersistedState(int Version, DateTimeOffset NextAttemptAt, int Failures,
        ClaudeCliQuotaStatus LastStatus, PersistedReading? LastSuccess, DateTimeOffset? LastAttemptAt = null);

    /// <summary>
    /// Restores the schedule a previous process saved. Only the next attempt time,
    /// the failure count, the last status and the last successful percentages and
    /// reset times are kept — no account, plan or CLI output. An unreadable file is
    /// ignored; a wait beyond the longest schedule (a clock moved backwards) is capped.
    /// </summary>
    private void EnsureStateLoaded()
    {
        if (_stateLoaded) return;
        _stateLoaded = true;
        try
        {
            var file = new FileInfo(_stateFile!);
            if (!file.Exists || file.Length > MaximumStateBytes) return;
            var state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(file.FullName), StateJson);
            if (state is not { Version: StateVersion } || state.Failures is < 0 or > 1000
                || !Enum.IsDefined(state.LastStatus) || state.LastStatus == ClaudeCliQuotaStatus.Disabled) return;
            var now = _clock.GetUtcNow();
            var longestWait = new[] { MinimumInterval, NotAvailableRetry, FailureBackoff[^1] }.Max();
            _nextAttemptAt = state.NextAttemptAt > now + longestWait ? now + longestWait : state.NextAttemptAt;
            _failures = state.Failures;
            _lastAttemptAt = state.LastAttemptAt is { } attemptedAt && attemptedAt <= now ? attemptedAt : null;
            // A reading from the future (the clock moved backwards) would look recent forever.
            _lastSuccess = state.LastSuccess is { } saved && saved.ObservedAt <= now ? Restore(saved) : null;
            var success = state.LastStatus is ClaudeCliQuotaStatus.Fresh or ClaudeCliQuotaStatus.NoWindows;
            // A success whose reading was not restored still keeps the saved wait above,
            // so a damaged file never lifts the rate-limit throttle.
            _lastAttempt = success && _lastSuccess is not null
                ? _lastSuccess
                : new(success ? ClaudeCliQuotaStatus.Unavailable : state.LastStatus, null, null, null, state.LastStatus switch
                {
                    ClaudeCliQuotaStatus.NotInstalled => "Claude CLI 后台额度：未找到 Claude Code CLI",
                    ClaudeCliQuotaStatus.NotAvailable => "Claude CLI 后台额度：当前 CLI 登录没有可用的订阅额度（未登录、API 密钥或第三方服务）",
                    _ when success => "Claude CLI 后台额度：上次结果无法恢复，稍后自动重试",
                    _ => "Claude CLI 后台额度：上次查询失败，稍后自动重试"
                });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // A damaged schedule only costs one extra query.
        }
    }

    private static ClaudeCliQuotaReading? Restore(PersistedReading saved)
    {
        static RateLimitWindow? Window(PersistedWindow? window, int minutes, DateTimeOffset observedAt) =>
            window is { UsedPercent: >= 0 and <= 100 } valid
                ? new(valid.UsedPercent, minutes, valid.ResetsAt, observedAt, ClaudeCliQuotaParser.Source)
                : null;
        var primary = Window(saved.Primary, 300, saved.ObservedAt);
        var secondary = Window(saved.Secondary, 10080, saved.ObservedAt);
        return primary is null && secondary is null
            ? new(ClaudeCliQuotaStatus.NoWindows, null, null, saved.ObservedAt, "Claude CLI 后台额度：查询成功，没有一般额度窗口")
            : new(ClaudeCliQuotaStatus.Fresh, primary, secondary, saved.ObservedAt, $"Claude CLI 后台额度：读取于 {saved.ObservedAt:O}");
    }

    private void SaveState()
    {
        if (_stateFile is null || _lastAttempt is null) return;
        var temporary = $"{_stateFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            static PersistedWindow? Window(RateLimitWindow? window) =>
                window is null ? null : new(window.UsedPercent, window.ResetsAt);
            var success = _lastSuccess is { ObservedAt: { } observedAt }
                ? new PersistedReading(observedAt, Window(_lastSuccess.Primary), Window(_lastSuccess.Secondary))
                : null;
            var state = new PersistedState(StateVersion, _nextAttemptAt, _failures, _lastAttempt.Status, success, _lastAttemptAt);
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, StateJson));
            File.Move(temporary, _stateFile, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Best effort: the in-memory schedule still applies to this process.
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static string WorkingDirectory()
    {
        // A stable, empty directory: the CLI keys per-project state by working
        // directory, and an isolated one keeps project files out of its startup.
        var directory = Path.Combine(Path.GetTempPath(), "codexU-claude-cli");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static ProcessStartInfo StartInfo(string executable, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    internal static string ControlFrame(string requestId)
    {
        // Written directly so the frame never depends on reflection-based serialization.
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "control_request");
            writer.WriteString("request_id", requestId);
            writer.WriteStartObject("request");
            writer.WriteString("subtype", "get_usage");
            writer.WriteBoolean("skip_behaviors", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Writes the single control frame, returns the matching control_response line,
    /// then closes stdin so the CLI exits without ever having received a user turn.
    /// </summary>
    internal static async Task<string> RunProcessAsync(ProcessStartInfo startInfo, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        Task? stderr = null;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("CLI did not start.");
            // Drain stderr so a chatty CLI can never block on a full pipe; it is not read.
            stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await process.StandardInput.WriteAsync(ControlFrame(requestId) + "\n");
            await process.StandardInput.FlushAsync(cancellationToken);

            var reader = new BoundedLineReader(process.StandardOutput.BaseStream, ClaudeCliQuotaParser.MaximumResponseBytes);
            string? response = null;
            for (var count = 0; count < MaximumStdoutLines && response is null; count++)
            {
                var line = await reader.ReadAsync(cancellationToken);
                if (line.IsEndOfStream) break;
                if (!line.IsTooLong && line.Line is { } text && ClaudeCliQuotaParser.IsResponseFor(text, requestId)) response = text;
            }
            process.StandardInput.Close();
            if (response is null) throw new InvalidDataException("No matching control response.");

            using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            exit.CancelAfter(ExitGracePeriod);
            try { await process.WaitForExitAsync(exit.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            return response;
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            // AggregateException: part of the tree could not be ended; never let it mask the result.
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                or NotSupportedException or AggregateException)
            { }
            if (stderr is not null)
            {
                try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException
                    or TimeoutException or ObjectDisposedException or InvalidOperationException)
                { }
            }
        }
    }
}
