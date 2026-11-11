using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeDesktopToolIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BoundNativeQuery_OwnsTheWholeSnapshotIncludingMissingWindowsAndAccount()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync();
        await fixture.WriteQueryAsync(new
        {
            status = "ok",
            plan = "untrusted price label",
            windows = new[] { new { label = "5-hour limit", percentUsed = 27, resetsAt = Now.AddHours(2).ToString("O") } }
        }, Now.AddMinutes(-1));

        var result = await fixture.Reader().ReadAsync();

        Assert.Equal(27, result.PrimaryQuota?.UsedPercent);
        Assert.Equal(Now.AddHours(2), result.PrimaryQuota?.ResetsAt);
        Assert.Equal("claude-desktop-get-usage", result.PrimaryQuota?.Source);
        Assert.Equal(new ClaudeQuotaState(ClaudeQuotaSource.DesktopQuery, ClaudeTranscriptStatus.Fresh), result.ClaudeQuotaState);
        Assert.Equal(ClaudeQuotaAction.None, result.ClaudeQuotaState!.Action);
        Assert.Equal(Now.AddMinutes(-1), result.ClaudeDesktopQueryObservedAt);
        Assert.Null(result.SecondaryQuota);
        Assert.Null(result.Account);
    }

    [Fact]
    public async Task FreshSuccessfulEmptyQuery_DoesNotRestoreOldHistoryWindows()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync();
        await fixture.WriteQueryAsync(new { status = "ok", windows = Array.Empty<object>() }, Now);

        var result = await fixture.Reader().ReadAsync();

        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
        Assert.Null(result.Account);
        Assert.Contains(result.Diagnostics, line => line.Contains("没有仍有效的一般额度窗口", StringComparison.Ordinal));
        Assert.Equal(new ClaudeQuotaState(ClaudeQuotaSource.DesktopQuery, ClaudeTranscriptStatus.NoWindows), result.ClaudeQuotaState);
        Assert.Equal(ClaudeQuotaAction.Query, result.ClaudeQuotaState!.Action);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrStaleQuery_FallsBackToClearlyIdentifiedHistory(bool stale)
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync();
        await fixture.WriteQueryAsync(new
        {
            status = stale ? "ok" : "unavailable",
            windows = new[] { new { label = "5-hour limit", percentUsed = 27, resetsAt = Now.AddHours(2).ToString("O") } }
        }, stale ? Now.AddMinutes(-16) : Now);

        var result = await fixture.Reader().ReadAsync();

        Assert.Null(result.PrimaryQuota);
        Assert.Equal(42, result.SecondaryQuota?.UsedPercent);
        Assert.Equal("claude-desktop-history", result.SecondaryQuota?.Source);
        Assert.Equal(new ClaudeQuotaState(ClaudeQuotaSource.DesktopHistory,
            stale ? ClaudeTranscriptStatus.Stale : ClaudeTranscriptStatus.Unavailable), result.ClaudeQuotaState);
        Assert.Equal(ClaudeQuotaAction.Query, result.ClaudeQuotaState!.Action);
        Assert.Equal(stale ? Now.AddMinutes(-16) : Now, result.ClaudeDesktopQueryObservedAt);
        Assert.Null(result.SecondaryQuota?.ResetsAt);
        Assert.Contains(result.Diagnostics, line => line.Contains("Claude Desktop 原生额度", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoundSessionWithoutResultUsesStatusLineUntilTranscriptExceedsLimit()
    {
        using var fixture = new Fixture();
        var dir = Path.Combine(fixture.Root, "data", "claude-code");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "statusline-snapshot.json"), JsonSerializer.Serialize(new
        {
            capturedAt = Now,
            primary = new { usedPercent = 12, windowDurationMinutes = 300 }
        }));
        await File.WriteAllTextAsync(fixture.Transcript, "");

        var missing = await fixture.Reader().ReadAsync();
        Assert.Equal(new ClaudeQuotaState(ClaudeQuotaSource.StatusLine, ClaudeTranscriptStatus.MissingResult), missing.ClaudeQuotaState);
        Assert.Equal(ClaudeQuotaAction.Query, missing.ClaudeQuotaState!.Action);
        using (var stream = File.OpenWrite(fixture.Transcript)) stream.SetLength(32 * 1024 * 1024 + 1);
        var oversized = await fixture.Reader().ReadAsync();
        Assert.Equal(new ClaudeQuotaState(ClaudeQuotaSource.StatusLine, ClaudeTranscriptStatus.TooLarge), oversized.ClaudeQuotaState);
        Assert.Equal(ClaudeQuotaAction.Rebind, oversized.ClaudeQuotaState!.Action);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-native-quota-integration-" + Guid.NewGuid().ToString("N"));
        private readonly string _session = Guid.NewGuid().ToString("D");
        internal string Transcript => Path.Combine(Root, _session + ".jsonl");
        private string Data => Path.Combine(Root, "data");
        private string Desktop => Path.Combine(Root, "Claude-Data");
        internal ClaudeCodeUsageReader Reader() => new(new CodexPaths(Root, Root,
            Path.Combine(Root, "missing.db"), Root, Root, Root, Root, Path.Combine(Root, ".claude"), Desktop),
            applicationDataDirectory: Data, claudeDesktopUsageTranscript: Transcript)
        { Clock = new FixedClock() };

        internal async Task WriteHistoryAsync()
        {
            var folder = Path.Combine(Desktop, "claude_fixture");
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "plan-usage-history.json"), JsonSerializer.Serialize(new
            {
                version = 2,
                samples = new[] { new { t = Now.ToUnixTimeMilliseconds(), org = "synthetic", u = new { sd = 42 } } }
            }));
        }

        internal async Task WriteQueryAsync(object plan, DateTimeOffset observedAt)
        {
            Directory.CreateDirectory(Root);
            var use = JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = _session,
                timestamp = observedAt,
                message = new { role = "assistant", content = new[] { new { type = "tool_use", id = "bound-call", name = ClaudeDesktopToolUsageParser.ToolName, input = new { } } } }
            });
            var result = JsonSerializer.Serialize(new
            {
                type = "user",
                sessionId = _session,
                timestamp = observedAt,
                message = new { role = "user", content = new[] { new { type = "tool_result", tool_use_id = "bound-call", content = JsonSerializer.Serialize(new { plan }) } } }
            });
            await File.WriteAllLinesAsync(Transcript, [use, result]);
        }

        public void Dispose()
        {
            UsageReadContext.Invalidate(Data);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
