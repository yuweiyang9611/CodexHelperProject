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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Codex_DistributesDeduplicatedChildTokens_AndRetainsMetadataAfterDeletion(bool hasLegacy)
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
            if (hasLegacy)
                await new DailyUsageHistoryStore(root).SaveAsync(AgentRuntime.Codex,
                    [new(DateOnly.FromDateTime(DateTime.Today), new(200, 0, 0, 0, 200), 0, 0, DataQuality.Detailed)], "all");
            var reader = new CodexSessionReader(paths, indexDirectory: root, historyEnabled: true);
            var first = await reader.ReadAsync();
            var today = Assert.Single(first.DailyUsage, d => d.Tokens > 0);
            Assert.Equal(hasLegacy ? 200 : 140, today.Tokens);
            if (hasLegacy)
                Assert.Contains(new UsageDistributionSlice("unknown", "unknown", 60), today.Distribution!);
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

    [Theory]
    [InlineData(AgentRuntime.Codex)]
    [InlineData(AgentRuntime.ClaudeCode)]
    public async Task Legacy_PreservesKnownSlices_AndOnlyUnattributedRemainder(AgentRuntime runtime)
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var history = new DailyUsageHistoryStore(root);
            var saved = new DailyUsageRecord(today, new(900, 200, 100, 0, 1000), 42, 123, DataQuality.Detailed);
            await history.SaveAsync(runtime, [saved], "all");
            var rows = new AttributedUsage[] {
                new("main", root, today, "MODEL-A", new(500, 0, 0, 0, 500), 1, Feature: "tasks"),
                new("child", root, today, "model-a", new(200, 0, 0, 0, 200), 1, SourceKind: "retained", Feature: "subagents"),
                new("missing-feature", root, today, "model-b", new(50, 0, 0, 0, 50), 1),
                new("missing-both", root, today, "unknown", new(50, 0, 0, 0, 50), 1) };
            var baseline = await UsageHistoryProjection.BuildAsync(runtime, [], root, null, DataQuality.Detailed, null, false, 0, 0, default);
            var projection = await UsageHistoryProjection.BuildAsync(runtime, rows, root, null, DataQuality.Detailed, null, false, 1, 0, default);
            var day = projection.Daily.Last();
            Assert.Equal("legacy", day.Source);
            Assert.Equal(1000, day.Tokens);
            Assert.Equal(42, day.CreditsUsed);
            Assert.Equal(DataQuality.Approximate, day.Quality);
            var slices = Assert.IsAssignableFrom<IReadOnlyList<UsageDistributionSlice>>(day.Distribution);
            Assert.Equal(1000, slices.Sum(s => s.Tokens));
            Assert.Equal(new UsageDistributionSlice("model-a", "tasks", 500), Assert.Single(slices, s => s.Feature == "tasks"));
            Assert.Equal(new UsageDistributionSlice("model-a", "subagents", 200), Assert.Single(slices, s => s.Feature == "subagents"));
            Assert.Contains(new UsageDistributionSlice("model-b", "unknown", 50), slices);
            Assert.Equal(new UsageDistributionSlice("unknown", "unknown", 250), Assert.Single(slices, s => s.Model == "unknown"));
            foreach (var (before, after) in new[] { (baseline.Tokens.Today, projection.Tokens.Today),
                (baseline.Tokens.SevenDays, projection.Tokens.SevenDays), (baseline.Tokens.Month, projection.Tokens.Month),
                (baseline.Tokens.Lifetime, projection.Tokens.Lifetime) })
            {
                Assert.Equal(before.Tokens, after.Tokens);
                Assert.Equal(before.Breakdown, after.Breakdown);
                Assert.Equal(before.CreditsUsed, after.CreditsUsed);
                Assert.Equal(before.UnratedTokens, after.UnratedTokens);
            }
            // Backfilling a view must not rewrite the only surviving daily total.
            Assert.Equal(saved, Assert.Single(await history.LoadAsync(runtime, "all")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Legacy_BackfillRequiresMatchingRuntimeDateWorkspace_AndUnambiguousSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var workspace = Path.Combine(root, "Repo");
            var scope = DailyUsageHistoryStore.ScopeFingerprint(workspace);
            var history = new DailyUsageHistoryStore(root);
            await history.SaveAsync(AgentRuntime.Codex, [new(today, new(1000, 0, 0, 0, 1000), 10, 0, DataQuality.Detailed)], scope);
            await history.SaveAsync(AgentRuntime.Codex, [new(today, new(9000, 0, 0, 0, 9000), 90, 0, DataQuality.Detailed)], "all");
            await history.SaveAsync(AgentRuntime.ClaudeCode, [new(today, new(8000, 0, 0, 0, 8000), 80, 0, DataQuality.Detailed)], scope);
            AttributedUsage Row(string id, string? cwd, DateOnly date, long tokens, string kind = "live") =>
                new(id, cwd, date, id, new(tokens, 0, 0, 0, tokens), 1, SourceKind: kind, Feature: "tasks");
            var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.Codex,
                [Row("same", workspace, today, 300), Row("nested", Path.Combine(workspace, "child"), today, 200),
                Row("other", workspace + "2", today, 5000), Row("unknown", null, today, 5000),
                Row("conflicting", workspace, today, 100, "conflict"), Row("yesterday", workspace, today.AddDays(-1), 50)],
                root, workspace, DataQuality.Partial, null, false, 0, 1, default);
            var day = projection.Daily.Last();
            Assert.Equal(1000, day.Tokens);
            Assert.Contains(new UsageDistributionSlice("same", "tasks", 300), day.Distribution!);
            Assert.Contains(new UsageDistributionSlice("nested", "tasks", 200), day.Distribution!);
            Assert.Contains(new UsageDistributionSlice("unknown", "unknown", 500), day.Distribution!);
            Assert.Equal(3, day.Distribution!.Count);
            Assert.Equal(50, projection.Daily[^2].Tokens);
            Assert.Equal(1050, projection.Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(1200)]
    public async Task CompleteLogs_TakePrecedenceOverLegacy_WithoutNegativeRemainder(long tokens)
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            await new DailyUsageHistoryStore(root).SaveAsync(AgentRuntime.Codex,
                [new(today, new(1000, 0, 0, 0, 1000), 10, 0, DataQuality.Detailed)], "all");
            var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.Codex,
                [new("main", root, today, "model-a", new(tokens, 0, 0, 0, tokens), 1, Feature: "tasks")],
                root, null, DataQuality.Detailed, null, false, 0, 0, default);
            var day = projection.Daily.Last();
            Assert.Equal("live", day.Source);
            Assert.Equal(tokens, day.Tokens);
            Assert.Equal(new UsageDistributionSlice("model-a", "tasks", tokens), Assert.Single(day.Distribution!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OriginalLog_BackfillsOldLedgerMetadata_AndRetainsClassification()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-distribution-" + Guid.NewGuid().ToString("N"));
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        try
        {
            var path = Path.Combine(sessions, "rollout-original.jsonl");
            await File.WriteAllLinesAsync(path, [
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "original", source = "cli", cwd = root } }),
                Model("model-a"), Tokens(80)]);
            var parsed = await CodexSessionReader.ParseFileAsync(path);
            await new UsageHistoryLedger(root).MergeAsync("codex",
                new Dictionary<string, ParsedSessionFile> { ["original"] = parsed with { Feature = "unknown" } },
                (_, _) => true, default);
            await new DailyUsageHistoryStore(root).SaveAsync(AgentRuntime.Codex,
                [new(DateOnly.FromDateTime(DateTime.Today), new(100, 0, 0, 0, 100), 0, 0, DataQuality.Detailed)], "all");
            var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");
            var reader = new CodexSessionReader(paths, indexDirectory: root, historyEnabled: true);
            var snapshot = await reader.ReadAsync();
            var day = Assert.Single(snapshot.DailyUsage, d => d.Tokens > 0);
            Assert.Equal(100, day.Tokens);
            Assert.Contains(new UsageDistributionSlice("model-a", "tasks", 80), day.Distribution!);
            Assert.Contains(new UsageDistributionSlice("unknown", "unknown", 20), day.Distribution!);
            Assert.Equal(2, day.Distribution!.Count);
            File.Delete(path);
            var retained = await reader.ReadAsync();
            Assert.Equal(day.Distribution, Assert.Single(retained.DailyUsage, d => d.Tokens > 0).Distribution);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Model(string model) => JsonSerializer.Serialize(new { type = "turn_context", payload = new { model } });
    private static string Tokens(int tokens) => JsonSerializer.Serialize(new
    {
        timestamp = DateTimeOffset.Now,
        type = "event_msg",
        payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = tokens, output_tokens = 0, total_tokens = tokens } } }
    });
}
