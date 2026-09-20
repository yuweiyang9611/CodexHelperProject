using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageDistributionTests
{
    [Fact]
    public async Task Claude_UsesSessionIdentityForTaskAndSubagentDistribution()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        var projects = Path.Combine(root, "claude", "projects");
        Directory.CreateDirectory(projects);
        try
        {
            foreach (var (agent, tokens) in new[] { ("", 70), ("child", 30) })
            {
                var line = JsonSerializer.Serialize(new
                {
                    type = "assistant",
                    sessionId = "session",
                    agentId = agent == "" ? null : agent,
                    cwd = root,
                    timestamp = DateTimeOffset.Now,
                    message = new { model = "claude-test", usage = new { input_tokens = tokens, output_tokens = 0 } }
                });
                await File.WriteAllTextAsync(Path.Combine(projects, "transcript-" + agent + ".jsonl"), line + "\n");
            }
            var paths = new CodexPaths(root, root, "missing.db", "missing", "missing", "missing", "missing", Path.Combine(root, "claude"));
            var snapshot = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root).ReadAsync();
            var day = Assert.Single(snapshot.DailyUsage, d => d.Tokens > 0);
            Assert.Equal(100, day.Tokens);
            Assert.Contains(day.Distribution!, s => s.Feature == "tasks" && s.Tokens == 70);
            Assert.Contains(day.Distribution!, s => s.Feature == "subagents" && s.Tokens == 30);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Codex_DistributesDeduplicatedChildTokens_AndRetainsMetadataAfterDeletion()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var parent = Path.Combine(sessions, "rollout-parent.jsonl");
            var child = Path.Combine(sessions, "rollout-child.jsonl");
            var meta = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "parent", thread_source = "user", cwd = root } });
            await File.WriteAllLinesAsync(parent, [meta, Model("model-a"), Tokens(100)]);
            await File.WriteAllLinesAsync(child, [
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "child", thread_source = "subagent", cwd = root,
                    source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent" } } }, parent_thread_id = "parent" } }),
                meta, Model("model-a"), Tokens(100),
                """{"type":"inter_agent_communication_metadata"}""", Model("model-b"), Tokens(140)]);
            var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");
            var reader = new CodexSessionReader(paths, indexDirectory: root, historyEnabled: true);
            var first = await reader.ReadAsync();
            var today = Assert.Single(first.DailyUsage, d => d.Tokens > 0);
            Assert.Equal(140, today.Tokens);
            Assert.Contains(today.Distribution!, s => s.Model == "model-a" && s.Feature == "tasks" && s.Tokens == 100);
            Assert.Contains(today.Distribution!, s => s.Model == "model-b" && s.Feature == "subagents" && s.Tokens == 40);
            Assert.Equal(today.Tokens, today.Distribution!.Sum(s => s.Tokens));
            File.Delete(parent);
            File.Delete(child);
            var retained = await reader.ReadAsync();
            Assert.Equal(today.Distribution, Assert.Single(retained.DailyUsage, d => d.Tokens > 0).Distribution);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Projection_GroupsModelsWithoutChangingTotals()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var rows = new AttributedUsage[] {
                new("a", null, today, "MODEL-A", new(70, 0, 0, 0, 70), 1, Feature: "tasks"),
                new("b", null, today, "model-a", new(30, 0, 0, 0, 30), 1, Feature: "subagents") };
            var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.Codex, rows, root, null, DataQuality.Detailed, null, false, 0, 0, default);
            var day = projection.Daily.Last();
            Assert.Equal(100, day.Distribution!.Sum(s => s.Tokens));
            Assert.All(day.Distribution!, s => Assert.Equal("model-a", s.Model));
            Assert.Equal(100, projection.Tokens.Today.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("\"cli\"", "tasks")]
    [InlineData("{\"subagent\":\"review\"}", "subagents")]
    [InlineData("\"future-source\"", "unknown")]
    public async Task Parser_ClassifiesOnlyKnownHeaderSources(string source, string expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "{\"type\":\"session_meta\",\"payload\":{\"id\":\"test\",\"source\":" + source + "}}\n");
            Assert.Equal(expected, (await CodexSessionReader.ParseFileAsync(path)).Feature);
        }
        finally { File.Delete(path); }
    }

    private static string Model(string model) => JsonSerializer.Serialize(new { type = "turn_context", payload = new { model } });
    private static string Tokens(int tokens) => JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.Now,
        type = "event_msg",
        payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = tokens, output_tokens = 0, total_tokens = tokens } } }
    });
}
