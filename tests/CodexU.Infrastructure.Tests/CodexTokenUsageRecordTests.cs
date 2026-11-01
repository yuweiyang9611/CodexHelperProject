using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexTokenUsageRecordTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParseFileAsync_MixedFormatsDoNotDoubleCountAndRecordOnlyTailIsIncluded(bool legacyFirst)
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "mixed.jsonl");
            var first = Record(100, 100, "2026-10-30T12:00:00Z");
            var twin = Legacy(100, 100, "2026-10-30T12:00:01Z");
            await File.WriteAllLinesAsync(path,
            [
                Meta(), Model("gpt-6.1-sol"), legacyFirst ? twin : first, legacyFirst ? first : twin,
                Model("gpt-6.1-terra"), Record(50, 150, "2026-10-30T12:02:00Z")
            ]);

            var parsed = await CodexSessionReader.ParseFileAsync(path);
            Assert.Equal(2, parsed.TokenEvents.Count);
            Assert.Equal(150, parsed.UsageBuckets.Sum(item => item.Tokens.InputTokens));
            Assert.Equal(30, parsed.UsageBuckets.Sum(item => item.Tokens.OutputTokens));
            Assert.Equal(30, parsed.UsageBuckets.Sum(item => item.Tokens.CachedInputTokens));
            Assert.Equal(100, Assert.Single(parsed.UsageBuckets, item => item.Model == "gpt-6.1-sol").Tokens.InputTokens);
            Assert.Equal(50, Assert.Single(parsed.UsageBuckets, item => item.Model == "gpt-6.1-terra").Tokens.InputTokens);
            Assert.All(parsed.UsageBuckets, item =>
            {
                Assert.Equal(0, item.Tokens.CacheWrite5mTokens);
                Assert.Equal(0, item.Tokens.CacheWrite1hTokens);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReadAsync_ResumesBetweenRecordAndLegacyTwinWithoutCountingAgain()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var path = Path.Combine(paths.SessionsDirectory, "rollout-record.jsonl");
            await File.WriteAllLinesAsync(path, [Meta(), Model("gpt-6.1-sol"), Record(100, 100)]);
            CodexSessionReader Reader() => new(paths, indexDirectory: root, historyEnabled: true);
            Assert.Equal(120, (await Reader().ReadAsync()).Tokens.Lifetime.Tokens);
            await File.AppendAllTextAsync(path, Legacy(100, 100) + "\n" + Record(50, 150) + "\n");
            var appended = await Reader().ReadAsync();
            Assert.Equal(1, appended.IndexStatus.IncrementalFiles);
            Assert.Equal(180, appended.Tokens.Lifetime.Tokens);
            Assert.Equal(0, appended.History!.Conflicts);
            Assert.Equal(180, (await Reader().ReadAsync()).Tokens.Lifetime.Tokens);

            // The previous index ignored records, so a version-14 cache must be reparsed.
            var index = Path.Combine(root, "session-index-v1.json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(index))!;
            json["version"] = 14;
            await File.WriteAllTextAsync(index, json.ToJsonString());
            var upgraded = await Reader().ReadAsync();
            Assert.Equal(1, upgraded.IndexStatus.ParsedFiles);
            Assert.Equal(180, upgraded.Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_MigratesOldLedgerOnlyWhenItsExactLegacyProjectionStillMatches(bool rewriteLegacy)
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var path = Path.Combine(paths.SessionsDirectory, "rollout-record.jsonl");
            // The unpaired record sits before a later legacy response. This changes
            // the event sequence, so the ordinary append-only ledger check rejects it.
            var lines = new[] { Meta(), Model("gpt-6.1-sol"), Legacy(100, 100), Record(50, 150), Legacy(25, 175) };
            await File.WriteAllLinesAsync(path, lines);
            var oldPath = Path.Combine(root, "old-parser.jsonl");
            await File.WriteAllLinesAsync(oldPath, lines.Select(line => line.Replace("token_usage_record", "ignored_usage_data", StringComparison.Ordinal)));
            Assert.Equal(new FileInfo(path).Length, new FileInfo(oldPath).Length);
            var old = await CodexSessionReader.ParseFileAsync(oldPath);
            old = old with { ParserVersion = 0 };
            var ledger = new UsageHistoryLedger(root);
            await ledger.MergeAsync("codex", new Dictionary<string, ParsedSessionFile> { ["thread-a"] = old }, (_, _) => true, default);
            if (rewriteLegacy)
                await File.WriteAllLinesAsync(path, lines.Select(line => line.Replace("\"input_tokens\":25", "\"input_tokens\":35", StringComparison.Ordinal)));

            var result = await new CodexSessionReader(paths, indexDirectory: root, historyEnabled: true).ReadAsync();
            Assert.Equal(rewriteLegacy ? 150 : 210, result.Tokens.Lifetime.Tokens);
            Assert.Equal(rewriteLegacy ? 1 : 0, result.History!.Conflicts);
            File.Delete(path);
            var retained = await new CodexSessionReader(paths, indexDirectory: root, historyEnabled: true).ReadAsync();
            Assert.Equal(result.Tokens.Lifetime.Tokens, retained.Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParseFileAsync_UsageRecordsHonorThreadSpawnReplayBoundary()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "fork.jsonl");
            await File.WriteAllLinesAsync(path,
            [
                """{"type":"session_meta","payload":{"id":"child","parent_thread_id":"thread-a","thread_source":"subagent","source":{"subagent":{"thread_spawn":{}}}}}""",
                Meta(), Model("gpt-6.1-sol"), Record(100, 100), Legacy(100, 100),
                """{"type":"inter_agent_communication_metadata","payload":{}}""", Record(50, 150)
            ]);
            var parsed = await CodexSessionReader.ParseFileAsync(path);
            Assert.Equal(60, Assert.Single(parsed.UsageBuckets).Tokens.VisibleTotalTokens);
            Assert.True(parsed.TokenEvents[0].IsStructuralReplay);
            Assert.False(parsed.TokenEvents[1].IsStructuralReplay);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static CodexPaths Paths(string root)
    {
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        return new(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");
    }

    private static string Meta() => """{"type":"session_meta","payload":{"id":"thread-a","source":"cli","thread_source":"user"}}""";
    private static string Model(string model) => JsonSerializer.Serialize(new { type = "turn_context", payload = new { model } });
    private static object Usage(long input) => new
    {
        input_tokens = input,
        cached_input_tokens = input / 5,
        output_tokens = input / 5,
        reasoning_output_tokens = input / 10,
        total_tokens = input + input / 5,
        cache_write_input_tokens = input / 10
    };
    private static string Record(long input, long total, string timestamp = "2026-10-30T12:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "token_usage_record",
            payload = new
            {
                thread_id = "thread-a",
                response_id = $"response-{total}",
                usage = Usage(input),
                thread_token_usage = Usage(total),
                turn_token_usage = Usage(input)
            }
        });
    private static string Legacy(long input, long total, string timestamp = "2026-10-30T12:00:01Z") =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new { last_token_usage = Usage(input), total_token_usage = Usage(total) }
            }
        });
}
