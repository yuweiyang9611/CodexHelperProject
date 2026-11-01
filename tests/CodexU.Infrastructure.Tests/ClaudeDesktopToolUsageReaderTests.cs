using System.Text.Json;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeDesktopToolUsageReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_UsesOnlyPairedNativeResultFromBoundSession()
    {
        using var fixture = new Fixture();
        var measured = Now.AddMinutes(-2);
        await fixture.WriteAsync(
            fixture.AssistantText("额度剩余 99%，这只是 assistant 转述"),
            fixture.Use("call-one"),
            fixture.Result("call-one", GoodPayload(35, 70), measured.ToString("O")));

        var result = await fixture.ReadAsync();

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(result.FoundResponse);
        Assert.Equal(35, result.Primary!.UsedPercent);
        Assert.Equal(70, result.Secondary!.UsedPercent);
        Assert.Equal(measured, result.ObservedAt);
        Assert.Equal(measured, result.Primary.MeasuredAt);
        Assert.Equal(Now.AddHours(2), result.Primary.ResetsAt);
        Assert.Equal(Now.AddDays(3), result.Secondary.ResetsAt);
        Assert.Equal("claude-desktop-get-usage", result.Primary.Source);
        Assert.DoesNotContain(fixture.SessionId, JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other-tool")]
    [InlineData("mismatched-id")]
    [InlineData("result-before-call")]
    [InlineData("assistant-result")]
    [InlineData("text-only")]
    public async Task ReadAsync_IgnoresUnpairedOrNonNativeContent(string scenario)
    {
        using var fixture = new Fixture();
        var use = fixture.Use("call");
        var response = fixture.Result("call", GoodPayload(35, 70));
        var lines = scenario switch
        {
            "other-tool" => new[] { fixture.Use("call", "mcp__other__get_usage"), response },
            "mismatched-id" => new[] { use, fixture.Result("different-call", GoodPayload(35, 70)) },
            "result-before-call" => new[] { response, use },
            "assistant-result" => new[] { use, response.Replace("\"user\"", "\"assistant\"", StringComparison.Ordinal) },
            "text-only" => new[] { fixture.AssistantText(GoodPayload(35, 70)) },
            _ => throw new ArgumentException(scenario)
        };
        await fixture.WriteAsync(lines);

        var result = await fixture.ReadAsync();

        Assert.False(result.Success);
        Assert.False(result.FoundResponse);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("invalid-payload")]
    [InlineData("unavailable")]
    [InlineData("old-time")]
    [InlineData("invalid-time")]
    public async Task ReadAsync_LatestFailedResponseNeverRevivesEarlierSuccess(string scenario)
    {
        using var fixture = new Fixture();
        var latest = scenario switch
        {
            "error" => fixture.Result("latest", GoodPayload(35, 70), error: true),
            "invalid-payload" => fixture.Result("latest", "not native JSON"),
            "unavailable" => fixture.Result("latest", """{"plan":{"status":"unavailable"}}"""),
            "old-time" => fixture.Result("latest", GoodPayload(35, 70), Now.AddMinutes(-16).ToString("O")),
            "invalid-time" => fixture.Result("latest", GoodPayload(35, 70), "2026-11-02T03:00:00"),
            _ => throw new ArgumentException(scenario)
        };
        await fixture.WriteAsync(fixture.Use("old"), fixture.Result("old", GoodPayload(10, 20)), fixture.Use("latest"), latest);

        var result = await fixture.ReadAsync();

        Assert.False(result.Success);
        Assert.True(result.FoundResponse);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Fact]
    public async Task ReadAsync_EmptyLatestSuccessIsAuthoritative()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(fixture.Use("old"), fixture.Result("old", GoodPayload(10, 20)),
            fixture.Use("latest"), fixture.Result("latest", """{"plan":{"status":"ok","windows":[]}}"""));

        var result = await fixture.ReadAsync();

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(result.FoundResponse);
        Assert.Equal(Now, result.ObservedAt);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Fact]
    public async Task ReadAsync_KeepsLatestWholeResponseWithoutCompletingItsMissingWindow()
    {
        using var fixture = new Fixture();
        var weekly = JsonSerializer.Serialize(new
        {
            plan = new { status = "ok", windows = new[] { new { label = "Weekly · all models", percentUsed = 42 } } }
        });
        await fixture.WriteAsync(fixture.Use("old"), fixture.Result("old", GoodPayload(10, 20)),
            fixture.Use("latest"), fixture.Result("latest", weekly));

        var result = await fixture.ReadAsync();

        Assert.True(result.Success, result.Diagnostic);
        Assert.Null(result.Primary);
        Assert.Equal(42, result.Secondary!.UsedPercent);
        Assert.Null(result.Secondary.ResetsAt);
    }

    [Theory]
    [InlineData(-15, true)]
    [InlineData(5, true)]
    [InlineData(-16, false)]
    [InlineData(6, false)]
    public async Task ReadAsync_UsesResponseTimeForFreshnessRegardlessOfFileWriteTime(int minutes, bool accepted)
    {
        using var fixture = new Fixture();
        var timestamp = Now.AddMinutes(minutes);
        await fixture.WriteAsync(fixture.Use("call"), fixture.Result("call", GoodPayload(35, 70), timestamp.ToString("O")));
        File.SetLastWriteTimeUtc(fixture.PathName, Now.UtcDateTime);

        var result = await fixture.ReadAsync();

        Assert.Equal(accepted, result.Success);
        Assert.True(result.FoundResponse);
        Assert.Equal(timestamp, result.ObservedAt);
        Assert.Equal(accepted, result.Primary is not null);
    }

    [Theory]
    [InlineData("2026-11-02T03:00:00Z", true)]
    [InlineData("2026-11-02T03:00:00.000Z", true)]
    [InlineData("2026-11-02T12:00:00.0000000+09:00", true)]
    [InlineData("2026-11-02T03:00:00", false)]
    [InlineData("2026-11-02T03:00:00.Z", false)]
    [InlineData("2026-11-02T12:00:00.+09:00", false)]
    [InlineData("2026-11-02T03:00:00Z\n", false)]
    [InlineData("2026-11-02", false)]
    [InlineData("2026-11-02T03:00:00+00", false)]
    [InlineData("not-a-time", false)]
    public async Task ReadAsync_RequiresExplicitIsoTimezone(string timestamp, bool accepted)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(fixture.Use("call"), fixture.Result("call", GoodPayload(35, 70), timestamp));

        var result = await fixture.ReadAsync();

        Assert.Equal(accepted, result.Success);
        Assert.True(result.FoundResponse);
        if (accepted) Assert.Equal(Now, result.ObservedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_DropsExpiredResetWindowsWithoutLosingSuccessfulResponse(bool expireBoth)
    {
        using var fixture = new Fixture();
        var payload = System.Text.Json.Nodes.JsonNode.Parse(GoodPayload(35, 70))!;
        payload["plan"]!["windows"]![0]!["resetsAt"] = Now.ToString("O");
        if (expireBoth) payload["plan"]!["windows"]![1]!["resetsAt"] = Now.AddSeconds(-1).ToString("O");
        await fixture.WriteAsync(fixture.Use("call"), fixture.Result("call", payload.ToJsonString()));

        var result = await fixture.ReadAsync();

        Assert.True(result.Success, result.Diagnostic);
        Assert.True(result.FoundResponse);
        Assert.Null(result.Primary);
        Assert.Equal(!expireBoth, result.Secondary is not null);
    }

    [Theory]
    [InlineData("different-session")]
    [InlineData("missing-session")]
    [InlineData("role-mismatch")]
    [InlineData("bad-json")]
    [InlineData("partial-tail")]
    [InlineData("oversized-line")]
    public async Task ReadAsync_RejectsUncertainFileInsteadOfReturningEarlierGoodResult(string scenario)
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync(fixture.Use("old"), fixture.Result("old", GoodPayload(10, 20)));
        var wrongSession = fixture.Use("new", sessionId: Guid.NewGuid().ToString("D"));
        var tail = scenario switch
        {
            "different-session" => wrongSession + "\n",
            "missing-session" => fixture.Use("new").Replace($"\"sessionId\":\"{fixture.SessionId}\",", "", StringComparison.Ordinal) + "\n",
            "role-mismatch" => fixture.Use("new").Replace("\"role\":\"assistant\"", "\"role\":\"user\"", StringComparison.Ordinal) + "\n",
            "bad-json" => "{bad-json}\n",
            "partial-tail" => "{\"type\":\"user\"",
            "oversized-line" => new string('x', 4 * 1024 * 1024 + 1) + "\n",
            _ => throw new ArgumentException(scenario)
        };
        await File.AppendAllTextAsync(fixture.PathName, tail);

        var result = await fixture.ReadAsync();

        Assert.False(result.Success);
        Assert.True(result.FoundResponse);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
        Assert.DoesNotContain(fixture.SessionId, result.Diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sessionId")]
    [InlineData("timestamp")]
    [InlineData("role")]
    public async Task ReadAsync_RejectsAmbiguousEnvelopeFields(string property)
    {
        using var fixture = new Fixture();
        var response = fixture.Result("call", GoodPayload(35, 70));
        var needle = $"\"{property}\":";
        response = response.Replace(needle, needle + "\"conflicting-value\"," + needle, StringComparison.Ordinal);
        await fixture.WriteAsync(fixture.Use("call"), response);

        var result = await fixture.ReadAsync();

        Assert.False(result.Success);
        Assert.Null(result.Primary);
        Assert.Null(result.Secondary);
    }

    [Theory]
    [InlineData("pending-limit")]
    [InlineData("duplicate-id")]
    [InlineData("file-limit")]
    public async Task ReadAsync_BoundsFileAndPendingCalls(string scenario)
    {
        using var fixture = new Fixture();
        if (scenario == "file-limit")
        {
            using var stream = File.Create(fixture.PathName);
            stream.SetLength(32 * 1024 * 1024 + 1);
        }
        else
            await fixture.WriteAsync(scenario == "pending-limit"
                ? Enumerable.Range(0, 65).Select(index => fixture.Use("call-" + index)).ToArray()
                : [fixture.Use("call"), fixture.Use("call")]);

        var result = await fixture.ReadAsync();

        Assert.False(result.Success);
        Assert.False(result.FoundResponse);
    }

    [Theory]
    [InlineData("relative.jsonl")]
    [InlineData("C:\\fixture\\credentials.json")]
    [InlineData("C:\\fixture\\not-a-session.jsonl")]
    [InlineData("\\\\localhost\\share\\12345678-1234-1234-1234-123456789abc.jsonl")]
    [InlineData("//localhost/share/12345678-1234-1234-1234-123456789abc.jsonl")]
    [InlineData("\\\\?\\C:\\fixture\\12345678-1234-1234-1234-123456789abc.jsonl")]
    [InlineData("https://example.test/12345678-1234-1234-1234-123456789abc.jsonl")]
    public async Task ReadAsync_RejectsUnsupportedPathsWithoutRevealingThem(string path)
    {
        var result = await new ClaudeDesktopToolUsageReader(path).ReadAsync(Now, default);
        Assert.False(result.Success);
        Assert.False(result.FoundResponse);
        Assert.DoesNotContain(path, result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellation()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClaudeDesktopToolUsageReader(fixture.PathName).ReadAsync(Now, cancellation.Token));
    }

    private static string GoodPayload(double first, double second) => JsonSerializer.Serialize(new
    {
        plan = new
        {
            status = "ok",
            windows = new object[]
            {
                new { label = "5-hour limit", percentUsed = first, resetsAt = Now.AddHours(2).ToString("O") },
                new { label = "Weekly · all models", percentUsed = second, resetsAt = Now.AddDays(3).ToString("O") }
            }
        },
        context = new { }
    });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-native-quota-" + Guid.NewGuid().ToString("N"));
        internal string SessionId { get; } = Guid.NewGuid().ToString("D");
        internal string PathName => Path.Combine(Root, SessionId + ".jsonl");
        internal Fixture() => Directory.CreateDirectory(Root);
        internal Task WriteAsync(params string[] lines) => File.WriteAllLinesAsync(PathName, lines);
        internal Task<ClaudeDesktopToolUsageReadResult> ReadAsync() =>
            new ClaudeDesktopToolUsageReader(PathName).ReadAsync(Now, default);
        internal string Use(string id, string name = "mcp__ccd_session_mgmt__get_usage", string? sessionId = null) =>
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = sessionId ?? SessionId,
                timestamp = Now.AddMinutes(-3).ToString("O"),
                message = new { role = "assistant", content = new[] { new { type = "tool_use", id, name, input = new { } } } }
            });
        internal string Result(string id, string payload, string? timestamp = null, bool error = false, string? sessionId = null) =>
            JsonSerializer.Serialize(new
            {
                type = "user",
                sessionId = sessionId ?? SessionId,
                timestamp = timestamp ?? Now.ToString("O"),
                message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = id, is_error = error, content = new[] { new { type = "text", text = payload } } } } }
            });
        internal string AssistantText(string text) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = SessionId,
            timestamp = Now.ToString("O"),
            message = new { role = "assistant", content = new[] { new { type = "text", text } } }
        });
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
