using System.Buffers;
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
/// One process-wide instance shares results between refreshes: callers within
/// <see cref="MinimumInterval"/> reuse the last attempt, concurrent callers share
/// one process, and failures back off before the CLI is started again.
/// </summary>
public sealed class ClaudeCliQuotaCollector
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan[] FailureBackoff =
        [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15)];
    internal static readonly TimeSpan NotAvailableRetry = TimeSpan.FromMinutes(60);
    internal static readonly string[] Arguments =
    [
        "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
        "--strict-mcp-config", "--no-session-persistence", "--safe-mode"
    ];
    private const int MaximumStdoutLines = 64;
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);

    public static ClaudeCliQuotaCollector Shared { get; } = new();

    private readonly Func<ClaudeCliExecutable?> _discover;
    private readonly Func<ClaudeCliExecutable, string, CancellationToken, Task<string>> _query;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;
    private readonly Lock _gate = new();
    private Task<ClaudeCliQuotaReading>? _inFlight;
    private ClaudeCliQuotaReading? _lastAttempt;
    private ClaudeCliQuotaReading? _lastSuccess;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _failures;

    private ClaudeCliQuotaCollector() : this(
        () => ClaudeCliAuthStatusReader.Discover(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH")),
        (executable, requestId, token) => RunProcessAsync(StartInfo(executable.Path, WorkingDirectory()), requestId, token),
        TimeProvider.System, TimeSpan.FromSeconds(20))
    { }

    internal ClaudeCliQuotaCollector(Func<ClaudeCliExecutable?> discover,
        Func<ClaudeCliExecutable, string, CancellationToken, Task<string>> query, TimeProvider clock, TimeSpan timeout)
    {
        _discover = discover;
        _query = query;
        _clock = clock;
        _timeout = timeout;
    }

    internal async Task<ClaudeCliQuotaReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<ClaudeCliQuotaReading>? attempt;
        lock (_gate)
        {
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
    /// Called when the CLI reports a login. A pending wait after "not logged in" or a
    /// failure is cleared so the next refresh queries at once; a recent success keeps
    /// its normal interval.
    /// </summary>
    public void RetrySoon()
    {
        lock (_gate)
        {
            if (_lastAttempt is not { Success: false }) return;
            _failures = 0;
            _nextAttemptAt = DateTimeOffset.MinValue;
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
            && now - readAt <= MaximumAge)
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
        }
        return reading;
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
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
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
