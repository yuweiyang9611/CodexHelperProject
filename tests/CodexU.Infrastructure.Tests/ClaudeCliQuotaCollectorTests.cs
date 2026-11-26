using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeCliQuotaCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 2, 44, 0, TimeSpan.Zero);
    private const string RequestId = "codexu-quota-fixture";

    [Fact]
    public void ParsesOnlyUnscopedSessionAndWeeklyWindowsFromTheObservedShape()
    {
        var reading = ClaudeCliQuotaParser.Parse(Response(RequestId, Limits(
            Window("session", 90, Now.AddHours(1)),
            Window("weekly_all", 40, Now.AddHours(3)),
            Window("weekly_scoped", 66, Now.AddHours(3), scoped: true))), RequestId, Now);

        Assert.Equal(ClaudeCliQuotaStatus.Fresh, reading.Status);
        Assert.Equal(new RateLimitWindow(90, 300, Now.AddHours(1), Now, "claude-cli-get-usage"), reading.Primary);
        Assert.Equal(new RateLimitWindow(40, 10080, Now.AddHours(3), Now, "claude-cli-get-usage"), reading.Secondary);
        Assert.Equal(Now, reading.ObservedAt);
    }

    [Fact]
    public void KeepsNoAccountPlanOrSpendFieldsAndAcceptsMicrosecondOffsets()
    {
        var line = Response(RequestId, Limits(Window("session", 12, null)), extra: new JsonObject
        {
            ["subscription_type"] = "private-plan",
            ["session"] = new JsonObject { ["total_cost_usd"] = 0, ["owner"] = "private-owner" }
        }).Replace("\"resets_at\":null", "\"resets_at\":\"2026-11-25T19:00:00.292807+00:00\"", StringComparison.Ordinal);

        var reading = ClaudeCliQuotaParser.Parse(line, RequestId, Now);

        Assert.Equal(new DateTimeOffset(2026, 11, 25, 19, 0, 0, TimeSpan.Zero).AddTicks(2_928_070), reading.Primary?.ResetsAt);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(reading), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"rate_limits_available":false,"rate_limits":null}""", ClaudeCliQuotaStatus.NotAvailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":null}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[]}}""", ClaudeCliQuotaStatus.NoWindows)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":"true","rate_limits":{"limits":[]}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[{"kind":"session","percent":1,"scope":null},{"kind":"session","percent":2,"scope":null}]}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[{"kind":"session","percent":-1,"scope":null}]}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[{"kind":"session","percent":"90","scope":null}]}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[{"kind":"session","percent":9,"resets_at":"2026-11-25 19:00","scope":null}]}}""", ClaudeCliQuotaStatus.Unavailable)]
    [InlineData("""{"rate_limits_available":true,"rate_limits":{"limits":[{"kind":"future_kind","percent":9,"scope":null}]}}""", ClaudeCliQuotaStatus.NoWindows)]
    public void DistinguishesUnavailablePlansMissingDataAndAmbiguousWindows(string usage, ClaudeCliQuotaStatus expected)
    {
        var line = $$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{RequestId}}}","response":{{{usage}}}}}""";
        var reading = ClaudeCliQuotaParser.Parse(line, RequestId, Now);
        Assert.Equal(expected, reading.Status);
        Assert.Null(reading.Primary);
        Assert.Null(reading.Secondary);
    }

    [Theory]
    [InlineData("""{"type":"control_response","response":{"subtype":"success","request_id":"other","response":{"rate_limits_available":true,"rate_limits":{"limits":[]}}}}""")]
    [InlineData("""{"type":"control_response","response":{"subtype":"error","request_id":"codexu-quota-fixture","error":"unsupported"}}""")]
    [InlineData("""{"type":"assistant","response":{"subtype":"success","request_id":"codexu-quota-fixture"}}""")]
    [InlineData("""{"type":"control_response","type":"control_response","response":{"subtype":"success","request_id":"codexu-quota-fixture"}}""")]
    [InlineData("not json")]
    public void RejectsResponsesThatAreNotThisRequestsSuccessfulControlResponse(string line)
    {
        Assert.Equal(ClaudeCliQuotaStatus.Unavailable, ClaudeCliQuotaParser.Parse(line, RequestId, Now).Status);
    }

    [Fact]
    public void ClampsOverfullWindowsToOneHundredPercent()
    {
        var reading = ClaudeCliQuotaParser.Parse(Response(RequestId, Limits(Window("session", 104, null))), RequestId, Now);
        Assert.Equal(100, reading.Primary?.UsedPercent);
    }

    [Fact]
    public async Task ReusesOneResultWithinTheMinimumIntervalThenQueriesAgain()
    {
        var clock = new MutableClock(Now);
        var calls = 0;
        var collector = Collector(clock, (_, id, _) =>
        {
            calls++;
            return Task.FromResult(Response(id, Limits(Window("session", 10 + calls, Now.AddHours(2)))));
        });

        Assert.Equal(11, (await collector.ReadAsync()).Primary?.UsedPercent);
        clock.Advance(ClaudeCliQuotaCollector.MinimumInterval - TimeSpan.FromSeconds(1));
        Assert.Equal(11, (await collector.ReadAsync()).Primary?.UsedPercent);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(12, (await collector.ReadAsync()).Primary?.UsedPercent);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOneCliProcess()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var collector = Collector(new MutableClock(Now), async (_, id, _) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return Response(id, Limits(Window("weekly_all", 40, Now.AddDays(1))));
        });

        var first = collector.ReadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = collector.ReadAsync();
        release.SetResult();

        Assert.Equal(40, (await first).Secondary?.UsedPercent);
        Assert.Equal(40, (await second).Secondary?.UsedPercent);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FailuresBackOffAndKeepTheLastRecentSuccessUntilItIsTooOld()
    {
        var clock = new MutableClock(Now);
        var fail = false;
        var calls = 0;
        var collector = Collector(clock, (_, id, _) =>
        {
            calls++;
            return fail ? throw new IOException("private-path")
                : Task.FromResult(Response(id, Limits(Window("session", 30, Now.AddHours(4)))));
        });
        await collector.ReadAsync();
        fail = true;

        clock.Advance(ClaudeCliQuotaCollector.MinimumInterval);
        var kept = await collector.ReadAsync();
        Assert.Equal(ClaudeCliQuotaStatus.Fresh, kept.Status);
        Assert.Equal(Now, kept.Primary?.MeasuredAt);
        Assert.Contains("稍后自动重试", kept.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path", kept.Diagnostic, StringComparison.Ordinal);

        clock.Advance(ClaudeCliQuotaCollector.FailureBackoff[0] - TimeSpan.FromSeconds(1));
        await collector.ReadAsync();
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await collector.ReadAsync();
        Assert.Equal(3, calls);

        // Past both the next backoff step and the maximum age of the last success.
        clock.Advance(TimeSpan.FromMinutes(12));
        var expired = await collector.ReadAsync();
        Assert.Equal(4, calls);
        Assert.True(clock.GetUtcNow() - Now > ClaudeCliQuotaCollector.MaximumAge);
        Assert.Equal(ClaudeCliQuotaStatus.Unavailable, expired.Status);
        Assert.Null(expired.Primary);
    }

    [Fact]
    public async Task NotSignedInWaitsAnHourAndNeverReusesTheEarlierAccountsWindows()
    {
        var clock = new MutableClock(Now);
        var calls = 0;
        var collector = Collector(clock, (_, id, _) => Task.FromResult(++calls == 1
            ? Response(id, Limits(Window("session", 30, Now.AddHours(4))))
            : NotSignedIn(id)));
        await collector.ReadAsync();

        clock.Advance(ClaudeCliQuotaCollector.MinimumInterval);
        var signedOut = await collector.ReadAsync();
        Assert.Equal(ClaudeCliQuotaStatus.NotAvailable, signedOut.Status);
        Assert.Null(signedOut.Primary);

        clock.Advance(ClaudeCliQuotaCollector.NotAvailableRetry - TimeSpan.FromSeconds(1));
        await collector.ReadAsync();
        Assert.Equal(2, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await collector.ReadAsync();
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ADetectedLoginEndsTheWaitButNotTheIntervalAfterASuccess()
    {
        var clock = new MutableClock(Now);
        var calls = 0;
        var signedIn = false;
        var collector = Collector(clock, (_, id, _) =>
        {
            calls++;
            return Task.FromResult(signedIn ? Response(id, Limits(Window("session", 20, Now.AddHours(4)))) : NotSignedIn(id));
        });
        Assert.Equal(ClaudeCliQuotaStatus.NotAvailable, (await collector.ReadAsync()).Status);

        signedIn = true;
        collector.RetrySoon();
        Assert.Equal(20, (await collector.ReadAsync()).Primary?.UsedPercent);
        Assert.Equal(2, calls);

        collector.RetrySoon();
        await collector.ReadAsync();
        Assert.Equal(2, calls);
    }

    private static string NotSignedIn(string requestId) =>
        """{"type":"control_response","response":{"subtype":"success","request_id":"REQUEST","response":{"rate_limits_available":false}}}"""
            .Replace("REQUEST", requestId, StringComparison.Ordinal);

    [Fact]
    public async Task MissingCliNeverStartsAProcess()
    {
        var collector = new ClaudeCliQuotaCollector(() => null,
            (_, _, _) => throw new InvalidOperationException("must not execute"), new MutableClock(Now), TimeSpan.FromSeconds(5));
        Assert.Equal(ClaudeCliQuotaStatus.NotInstalled, (await collector.ReadAsync()).Status);
    }

    [Fact]
    public async Task DeadlineTurnsAHungQueryIntoUnavailable()
    {
        var collector = new ClaudeCliQuotaCollector(() => new("fixture", ClaudeCliSource.Standalone),
            async (_, _, token) => { await Task.Delay(Timeout.Infinite, token); return ""; },
            TimeProvider.System, TimeSpan.FromMilliseconds(100));
        var reading = await collector.ReadAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ClaudeCliQuotaStatus.Unavailable, reading.Status);
        Assert.Contains("超时", reading.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsThatHaveResetSinceTheQueryAreDropped()
    {
        var clock = new MutableClock(Now);
        var collector = Collector(clock, (_, id, _) =>
            Task.FromResult(Response(id, Limits(Window("session", 70, Now.AddMinutes(1))))));
        Assert.Equal(ClaudeCliQuotaStatus.Fresh, (await collector.ReadAsync()).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        var reading = await collector.ReadAsync();
        Assert.Equal(ClaudeCliQuotaStatus.NoWindows, reading.Status);
        Assert.Null(reading.Primary);
    }

    [Fact]
    public void LaunchesThePrintStreamJsonModeWithoutSessionPersistenceOrExtraMcp()
    {
        var info = ClaudeCliQuotaCollector.StartInfo("fixture", "work");
        Assert.Equal(ClaudeCliQuotaCollector.Arguments, info.ArgumentList);
        Assert.Contains("--no-session-persistence", info.ArgumentList);
        Assert.Contains("--strict-mcp-config", info.ArgumentList);
        Assert.Equal("work", info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput);
        var frame = JsonNode.Parse(ClaudeCliQuotaCollector.ControlFrame(RequestId))!;
        Assert.Equal("control_request", frame["type"]!.GetValue<string>());
        Assert.Equal("get_usage", frame["request"]!["subtype"]!.GetValue<string>());
        Assert.True(frame["request"]!["skip_behaviors"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RealProcessReceivesOnlyTheControlFrameAndItsResponseIsMatched()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var stdinLog = Path.Combine(fixture.Root, "stdin.txt");
        // A stand-in CLI: echo an unrelated message first, answer the request, then
        // wait for stdin to close and record everything that was ever written to it.
        var script = string.Join("; ",
            "$first = [Console]::In.ReadLine()",
            "$id = ($first | ConvertFrom-Json).request_id",
            "[Console]::Out.WriteLine('{\"type\":\"system\",\"subtype\":\"noise\"}')",
            "$limits = '{\"rate_limits_available\":true,\"rate_limits\":{\"limits\":[{\"kind\":\"session\",\"percent\":55,\"scope\":null}]}}'",
            "[Console]::Out.WriteLine('{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"' + $id + '\",\"response\":' + $limits + '}}')",
            "[Console]::Out.Flush()",
            "$rest = [Console]::In.ReadToEnd()",
            $"[IO.File]::WriteAllText('{stdinLog.Replace("'", "''")}', $first + [char]10 + $rest)");
        var info = ClaudeCliQuotaCollector.StartInfo("powershell.exe", fixture.Root);
        info.ArgumentList.Clear();
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(argument);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var line = await ClaudeCliQuotaCollector.RunProcessAsync(info, RequestId, deadline.Token);

        Assert.Equal(55, ClaudeCliQuotaParser.Parse(line, RequestId, Now).Primary?.UsedPercent);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(stdinLog) && DateTime.UtcNow < until) await Task.Delay(20);
        var written = (await File.ReadAllTextAsync(stdinLog)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var frame = Assert.Single(written);
        Assert.Equal(ClaudeCliQuotaCollector.ControlFrame(RequestId), frame);
        Assert.DoesNotContain("\"user\"", frame, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealProcessThatNeverAnswersIsKilledAtTheDeadline()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var pidFile = Path.Combine(fixture.Root, "pid.txt");
        var info = ClaudeCliQuotaCollector.StartInfo("powershell.exe", fixture.Root);
        info.ArgumentList.Clear();
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            $"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30" })
            info.ArgumentList.Add(argument);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaudeCliQuotaCollector.RunProcessAsync(info, RequestId, deadline.Token));

        Assert.True(File.Exists(pidFile), "Synthetic child must have started.");
        var id = int.Parse(await File.ReadAllTextAsync(pidFile));
        try { Assert.True(Process.GetProcessById(id).HasExited); }
        catch (ArgumentException) { /* Already reaped. */ }
    }

    private static ClaudeCliQuotaCollector Collector(TimeProvider clock,
        Func<ClaudeCliExecutable, string, CancellationToken, Task<string>> query) =>
        new(() => new("fixture", ClaudeCliSource.DesktopEmbedded), query, clock, TimeSpan.FromSeconds(20));

    internal static string Response(string requestId, JsonArray limits, JsonObject? extra = null)
    {
        var usage = extra ?? new JsonObject();
        usage["rate_limits_available"] = true;
        usage["rate_limits"] = new JsonObject { ["limits"] = limits };
        return new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = usage }
        }.ToJsonString();
    }

    internal static JsonArray Limits(params JsonObject[] windows) => new(windows.Cast<JsonNode>().ToArray());

    internal static JsonObject Window(string kind, double percent, DateTimeOffset? resetsAt, bool scoped = false) => new()
    {
        ["kind"] = kind,
        ["percent"] = percent,
        ["resets_at"] = resetsAt?.ToString("O"),
        ["scope"] = scoped ? new JsonObject { ["model"] = new JsonObject { ["display_name"] = "Model" } } : null
    };

    internal sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-cli-quota-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { /* A just-killed child can briefly hold the directory. */ }
        }
    }
}
