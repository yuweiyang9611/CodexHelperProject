using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeHistoryCaptureTests
{
    [Fact]
    public async Task ReadAsync_RecordsEachMeasuredDayAndCanReplayItAfterTheSourceIsGone()
    {
        // The whole point of the store: the transcripts behind these numbers are
        // rotated and cleaned by Claude Code, so what the reader measured has to
        // outlive them. Deleting the source and reading the store back is the only
        // check that actually demonstrates that.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-capture-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var appData = Path.Combine(root, "appdata");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(appData);
        try
        {
            var transcript = Path.Combine(projectDirectory, "session.jsonl");
            var today = DateTimeOffset.Now;
            await File.WriteAllLinesAsync(transcript,
            [
                Line(root, today.AddDays(-2), 1_000, 400),
                Line(root, today.AddDays(-1), 2_000, 800),
                Line(root, today, 3_000, 1_200),
            ]);

            var paths = CreatePaths(root, claude);
            var rates = new[] { new ModelCreditRate("claude-opus-5", 100, 10, 200) };
            var reader = new ClaudeCodeUsageReader(
                paths,
                customRates: rates,
                applicationDataDirectory: appData);

            var live = await reader.ReadAsync();
            Assert.True(live.Tokens.Lifetime.Tokens > 0);

            // The source disappears, exactly as log rotation would do it.
            File.Delete(transcript);

            var afterRotation = await new ClaudeCodeUsageReader(
                paths,
                customRates: rates,
                applicationDataDirectory: appData).ReadAsync();
            Assert.Equal(0, afterRotation.Tokens.Lifetime.Tokens);

            var store = new DailyUsageHistoryStore(appData);
            var recorded = await store.LoadAsync(
                AgentRuntime.ClaudeCode,
                DailyUsageHistoryStore.ScopeFingerprint(null));

            Assert.Equal(3, recorded.Count);
            Assert.Equal(recorded.OrderBy(day => day.Date).Select(day => day.Date), recorded.Select(day => day.Date));
            Assert.All(recorded, day => Assert.True(day.Tokens.VisibleTotalTokens > 0));
            // Credits were priced while the source existed and survive without it.
            Assert.True(recorded.Sum(day => day.CreditsUsed) > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_KeepsRecordingWhenTheHistoryDirectoryCannotBeWritten()
    {
        // A history write must never be the reason a refresh fails.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-capture-ro-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(projectDirectory, "session.jsonl"),
                [Line(root, DateTimeOffset.Now, 500, 100)]);

            // A file where the history directory needs to be makes creation fail.
            var blocked = Path.Combine(root, "blocked");
            await File.WriteAllTextAsync(blocked, "not a directory");

            var result = await new ClaudeCodeUsageReader(
                CreatePaths(root, claude),
                applicationDataDirectory: blocked).ReadAsync();

            Assert.True(result.Tokens.Lifetime.Tokens > 0);
            Assert.Contains(result.Diagnostics, value => value.Contains("用量历史", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Line(string cwd, DateTimeOffset timestamp, long input, long output) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            cwd,
            timestamp = timestamp.ToString("O"),
            message = new
            {
                model = "claude-opus-5",
                usage = new { input_tokens = input, output_tokens = output }
            }
        });

    private static CodexPaths CreatePaths(string root, string claude) => new(
        root,
        Path.Combine(root, ".codex"),
        Path.Combine(root, ".codex", "state.sqlite"),
        Path.Combine(root, ".codex", "sessions"),
        Path.Combine(root, ".codex", "archived_sessions"),
        Path.Combine(root, ".codex", "automations"),
        Path.Combine(root, ".codex", "goals.sqlite"),
        claude);
}
