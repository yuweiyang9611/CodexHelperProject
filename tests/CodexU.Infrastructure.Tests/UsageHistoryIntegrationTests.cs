using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageHistoryIntegrationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Claude_RetainsMissingAndTruncatedSources_Reprices_AndDoesNotDoubleCountAppend()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var a = Path.Combine(paths.ClaudeDirectory, "projects", "a.jsonl");
            var b = Path.Combine(paths.ClaudeDirectory, "projects", "b.jsonl");
            var lineA = ClaudeLine(root, 100);
            await File.WriteAllTextAsync(a, lineA + "\n");
            await File.WriteAllTextAsync(b, ClaudeLine(root, 200) + "\n");
            var rates = new[] { new ModelCreditRate("claude-test", 100, 10, 200) };
            ClaudeCodeUsageReader Reader(IReadOnlyList<ModelCreditRate>? price = null, bool index = true) =>
                new(paths, customRates: price ?? rates, applicationDataDirectory: root, incrementalIndexEnabled: index);
            var first = await Reader().ReadAsync();
            var cached = await Reader().ReadAsync();
            Assert.Equal(2, cached.IndexStatus.ReusedFiles);
            Assert.Equal(0, cached.IndexStatus.ParsedFiles);
            Assert.Equal(first.Tokens.Lifetime.Tokens, cached.Tokens.Lifetime.Tokens);
            Assert.Equal(first.Tokens.Lifetime.CreditsUsed, cached.Tokens.Lifetime.CreditsUsed);
            File.Delete(b);
            await File.AppendAllTextAsync(a, ClaudeLine(root, 50) + "\n");
            var append = await Reader().ReadAsync();
            Assert.Equal(1, append.IndexStatus.IncrementalFiles);
            Assert.Equal(first.Tokens.Lifetime.Tokens + 50, append.Tokens.Lifetime.Tokens);
            await File.WriteAllTextAsync(a, lineA + "\n");
            var truncated = await Reader().ReadAsync();
            Assert.Equal(append.Tokens.Lifetime.Tokens, truncated.Tokens.Lifetime.Tokens);
            Assert.Equal(1, truncated.History!.Conflicts);
            File.Delete(a);
            var doubled = await Reader([new ModelCreditRate("claude-test", 200, 20, 400)]).ReadAsync();
            Assert.Equal(append.Tokens.Lifetime.Tokens, doubled.Tokens.Lifetime.Tokens);
            Assert.Equal(append.Tokens.Lifetime.CreditsUsed * 2, doubled.Tokens.Lifetime.CreditsUsed, 8);
            Assert.Equal(doubled.Tokens.Lifetime.Tokens, doubled.DailyUsage.Sum(d => d.Tokens));
            Assert.Equal(doubled.Tokens.Lifetime.Tokens, doubled.Models.Sum(m => m.Tokens));
            Assert.Equal(doubled.Tokens.Lifetime.CreditsUsed, doubled.Projects.Sum(p => p.CreditsUsed ?? 0), 8);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_PartialLineIsNotCommitted_AndLargeUnchangedIndexDoesNotParse()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var file = Path.Combine(paths.ClaudeDirectory, "projects", "large.jsonl");
            var line = ClaudeLine(root, 1);
            await File.WriteAllTextAsync(file, string.Concat(Enumerable.Repeat(line + "\n", 30000)) + line[..20]);
            ClaudeCodeUsageReader.ReadMetrics? metrics = null;
            var reader = new ClaudeCodeUsageReader(paths, applicationDataDirectory: root) { MetricsObserved = value => metrics = value };
            var first = await reader.ReadAsync();
            Assert.Equal(30000, first.Tokens.Lifetime.Tokens);
            Assert.Equal(30000, metrics!.ParsedLines);
            output.WriteLine($"Full: {metrics}");
            var context = UsageReadContext.For(root);
            var deserialized = context.LedgerDeserialized;
            var writes = context.LedgerWrites;
            var allocated = GC.GetTotalAllocatedBytes();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var unchanged = await reader.ReadAsync();
            output.WriteLine($"Complete unchanged refresh: {elapsed.Elapsed.TotalMilliseconds:F2} ms; allocated {GC.GetTotalAllocatedBytes() - allocated}; working set {Environment.WorkingSet}");
            Assert.Equal(deserialized, context.LedgerDeserialized);
            Assert.Equal(writes, context.LedgerWrites);
            Assert.Equal(0, metrics!.TranscriptBytes);
            Assert.Equal(0, metrics.ParsedLines);
            output.WriteLine($"Unchanged: {metrics}");
            Assert.Equal(1, unchanged.IndexStatus.ReusedFiles);
            Assert.Equal(0, unchanged.IndexStatus.ParsedFiles);
            await File.AppendAllTextAsync(file, line[20..] + "\n");
            var appended = await reader.ReadAsync();
            Assert.Equal(30001, appended.Tokens.Lifetime.Tokens);
            Assert.Equal(1, metrics!.ParsedLines);
            // Reading the committed prefix verifies no middle rewrite occurred;
            // only the newly completed JSON line is parsed.
            Assert.InRange(metrics.TranscriptBytes, 1, 2 * new FileInfo(file).Length + 32768);
            output.WriteLine($"Append: {metrics}");
            Assert.Equal(1, appended.IndexStatus.IncrementalFiles);
            var full = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root, incrementalIndexEnabled: false).ReadAsync();
            Assert.Equal(appended.Tokens.Lifetime.Tokens, full.Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Codex_FiltersAfterForkReconstruction_AndRetainsParentAfterDeletion()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var a = Path.Combine(root, "Repo"); var b = Path.Combine(root, "Repo2");
            var parent = Path.Combine(paths.SessionsDirectory, "rollout-parent.jsonl");
            var child = Path.Combine(paths.SessionsDirectory, "rollout-child.jsonl");
            await File.WriteAllLinesAsync(parent, [Meta("parent", a), CodexLine(100)]);
            await File.WriteAllLinesAsync(child, [Meta("child", b, "parent"), CodexLine(100), CodexLine(150)]);
            CodexSessionReader Reader(string? scope) => new(paths, indexDirectory: root, defaultWorkspace: scope, historyEnabled: true);
            var all = await Reader(null).ReadAsync();
            Assert.Equal(150, all.Tokens.Lifetime.Tokens);
            Assert.Equal(100, (await Reader(a + Path.DirectorySeparatorChar).ReadAsync()).Tokens.Lifetime.Tokens);
            Assert.Equal(50, (await Reader(b).ReadAsync()).Tokens.Lifetime.Tokens);
            File.Delete(parent);
            Assert.Equal(150, (await Reader(null).ReadAsync()).Tokens.Lifetime.Tokens);
            Assert.Equal(50, (await Reader(b).ReadAsync()).Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Ledger_BackupRestoreReplacesNewGeneration_AndLegacyRemainsUnpricedByModel()
    {
        var root = CreateRoot();
        try
        {
            var data = Path.Combine(root, "data"); Directory.CreateDirectory(data);
            var legacy = new DailyUsageHistoryStore(data);
            await legacy.SaveAsync(AgentRuntime.ClaudeCode, [new(DateOnly.FromDateTime(DateTime.Today),
                new TokenBreakdown(100, 0, 0, 0, 100), 12, 0, DataQuality.Detailed)], "all");
            var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.ClaudeCode, [], data, null,
                DataQuality.Detailed, null, false, 0, 0, default);
            Assert.Equal(12, projection.Tokens.Lifetime.CreditsUsed);
            Assert.Equal("legacy", projection.Daily.Last().Source);
            var settings = new AppSettingsStore(data); var todos = new TodoStore(data);
            var service = new LocalDataManagementService(settings, todos, data);
            var backup = Path.Combine(root, "backup.json");
            await service.BackupAsync(new AppSettings(), backup);
            var ledger = new UsageHistoryLedger(data);
            await ledger.MergeAsync("test", new Dictionary<string, int> { ["later"] = 1 }, (_, _) => true, default);
            var cache = Path.Combine(data, "claude-session-index-v1.json");
            await File.WriteAllTextAsync(cache, "stale");
            await service.RestoreAsync(backup);
            Assert.False(File.Exists(cache));
            var restored = await ledger.MergeAsync("test", new Dictionary<string, int>(), (_, _) => true, default);
            Assert.Empty(restored.Sources);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_WorkspaceBoundaryUnknownAndSubagents_UseSameUsageScope()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var repo = Path.Combine(root, "Repo");
            await File.WriteAllLinesAsync(Path.Combine(paths.ClaudeDirectory, "projects", "scopes.jsonl"),
                [ClaudeLine(repo, 10), ClaudeLine(repo + "2", 20), ClaudeLine(Path.Combine(repo, "child"), 30), ClaudeLine("", 40)]);
            var all = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root).ReadAsync();
            var scoped = await new ClaudeCodeUsageReader(paths, repo.ToUpperInvariant() + "\\", applicationDataDirectory: root).ReadAsync();
            var subagents = await new ClaudeCodeUsageReader(paths, repo, showSubagents: true, applicationDataDirectory: root).ReadAsync();
            Assert.Equal(100, all.Tokens.Lifetime.Tokens);
            Assert.Equal(40, scoped.Tokens.Lifetime.Tokens);
            Assert.Equal(scoped.Tokens.Lifetime.Tokens, subagents.Tokens.Lifetime.Tokens);
            Assert.Contains(all.Projects, p => p.FullPath is null && p.Tokens == 40);
            Assert.Contains(scoped.Diagnostics, d => d.Contains("工作区覆盖不足"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_ReadFailureAndRewrittenSource_PreservePriorRevisionAndAcceptIndependentSource()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var file = Path.Combine(paths.ClaudeDirectory, "projects", "original.jsonl");
            await File.WriteAllTextAsync(file, ClaudeLine(root, 100) + "\n");
            var first = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root).ReadAsync();
            using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failed = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root, incrementalIndexEnabled: false).ReadAsync();
                Assert.Equal(first.Tokens.Lifetime.Tokens, failed.Tokens.Lifetime.Tokens);
                Assert.Equal(1, failed.History!.RetainedSources);
            }
            await File.WriteAllTextAsync(file, ClaudeLine(root, 9999) + "\n");
            await File.WriteAllTextAsync(Path.Combine(paths.ClaudeDirectory, "projects", "independent.jsonl"), ClaudeLine(root, 25) + "\n");
            var rewritten = await new ClaudeCodeUsageReader(paths, applicationDataDirectory: root).ReadAsync();
            Assert.Equal(125, rewritten.Tokens.Lifetime.Tokens);
            Assert.Equal(1, rewritten.History!.Conflicts);
            Assert.Equal("conflict", rewritten.DailyUsage.Last().Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Codex_DivergentArchiveDoesNotReplaceKnownSource_AndSqliteSuppliesMissingWorkspace()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var repo = Path.Combine(root, "Repo");
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={paths.StateDatabase};Pooling=False"))
            {
                await db.OpenAsync();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE threads(id TEXT,cwd TEXT); INSERT INTO threads VALUES('session',$cwd)";
                command.Parameters.AddWithValue("$cwd", repo);
                await command.ExecuteNonQueryAsync();
            }
            var original = Path.Combine(paths.SessionsDirectory, "rollout-original.jsonl");
            await File.WriteAllLinesAsync(original, [Meta("session", ""), CodexLine(100)]);
            CodexSessionReader Reader() => new(paths, indexDirectory: root, defaultWorkspace: repo, historyEnabled: true);
            Assert.Equal(100, (await Reader().ReadAsync()).Tokens.Lifetime.Tokens);
            var context = UsageReadContext.For(root);
            var builds = context.ReconstructionBuilds;
            Assert.Equal(100, (await Reader().ReadAsync()).Tokens.Lifetime.Tokens);
            Assert.Equal(builds, context.ReconstructionBuilds);
            Directory.CreateDirectory(paths.ArchivedSessionsDirectory);
            await File.WriteAllLinesAsync(Path.Combine(paths.ArchivedSessionsDirectory, "rollout-conflict.jsonl"),
                [Meta("session", repo), CodexLine(900), CodexLine(1000)]);
            var conflict = await Reader().ReadAsync();
            Assert.Equal(100, conflict.Tokens.Lifetime.Tokens);
            Assert.Equal(1, conflict.History!.Conflicts);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Projection_KeepsAllDates_AndLabelsOnlyRetainedDays()
    {
        var root = CreateRoot();
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            AttributedUsage Entry(string id, DateOnly day, string kind) => new(id, root, day, "unknown",
                new TokenBreakdown(10, 0, 0, 0, 10), 1, SourceKind: kind);
            var projection = await UsageHistoryProjection.BuildAsync(AgentRuntime.Codex,
                [Entry("old", today.AddDays(-300), "retained"), Entry("yesterday", today.AddDays(-1), "retained"), Entry("today", today, "live")],
                root, null, DataQuality.Detailed, null, false, 2, 0, default);
            Assert.Equal(30, projection.Tokens.Lifetime.Tokens);
            Assert.Equal(20, projection.Tokens.SevenDays.Tokens);
            Assert.Equal(182, projection.Daily.Count);
            Assert.Equal("retained", projection.Daily[^2].Source);
            Assert.Equal("live", projection.Daily[^1].Source);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_LogicalSessionSurvivesRename_WithoutCountingCopiedFilesTwice()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var original = Path.Combine(paths.ClaudeDirectory, "projects", "original.jsonl");
            var renamed = Path.Combine(paths.ClaudeDirectory, "projects", "renamed.jsonl");
            var line = JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = "stable-session",
                cwd = root,
                timestamp = DateTimeOffset.Now.ToString("O"),
                message = new { model = "claude-test", usage = new { input_tokens = 10 } }
            });
            await File.WriteAllTextAsync(original, line + "\n");
            var reader = new ClaudeCodeUsageReader(paths, applicationDataDirectory: root);
            Assert.Equal(10, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
            File.Copy(original, renamed);
            Assert.Equal(10, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
            var context = UsageReadContext.For(root);
            var compared = context.SourceComparisons;
            Assert.Equal(10, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
            Assert.Equal(compared, context.SourceComparisons);
            File.Delete(original);
            Assert.Equal(10, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_MiddleRewriteFollowedByAppend_IsNotAcceptedAsAppend()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var file = Path.Combine(paths.ClaudeDirectory, "projects", "middle.jsonl");
            var line = ClaudeLine(root, 100);
            var lines = Enumerable.Repeat(line, 300).ToArray();
            await File.WriteAllLinesAsync(file, lines);
            var reader = new ClaudeCodeUsageReader(paths, applicationDataDirectory: root);
            Assert.Equal(30000, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
            lines[150] = line.Replace("\"input_tokens\":100", "\"input_tokens\":900", StringComparison.Ordinal);
            await File.WriteAllLinesAsync(file, lines.Append(ClaudeLine(root, 20)));
            var revised = await reader.ReadAsync();
            Assert.Equal(0, revised.IndexStatus.IncrementalFiles);
            Assert.Equal(30000, revised.Tokens.Lifetime.Tokens);
            Assert.Equal(1, revised.History!.Conflicts);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Claude_InvalidAppendedLineDoesNotDiscardNewValidUsage()
    {
        var root = CreateRoot();
        try
        {
            var paths = Paths(root);
            var file = Path.Combine(paths.ClaudeDirectory, "projects", "tolerant.jsonl");
            await File.WriteAllTextAsync(file, ClaudeLine(root, 10) + "\n");
            var reader = new ClaudeCodeUsageReader(paths, applicationDataDirectory: root);
            await reader.ReadAsync();
            await File.AppendAllTextAsync(file, "{invalid}\n" + ClaudeLine(root, 5) + "\n");
            var appended = await reader.ReadAsync();
            Assert.Equal(15, appended.Tokens.Lifetime.Tokens);
            Assert.Equal(0, appended.History!.Conflicts);
            Assert.Equal(DataQuality.Partial, appended.Tokens.Lifetime.Quality);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "claude", "projects"));
        Directory.CreateDirectory(Path.Combine(root, "sessions")); return root;
    }
    private static CodexPaths Paths(string root) => new(root, root, Path.Combine(root, "missing.sqlite"),
        Path.Combine(root, "sessions"), Path.Combine(root, "archives"), "missing", "missing", Path.Combine(root, "claude"));
    private static string ClaudeLine(string cwd, int input) => JsonSerializer.Serialize(new
    {
        type = "assistant",
        cwd,
        timestamp = DateTimeOffset.Now.ToString("O"),
        message = new { model = "claude-test", usage = new { input_tokens = input, output_tokens = 0 } }
    });
    private static string Meta(string id, string cwd, string? fork = null) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd, forked_from_id = fork } });
    private static string CodexLine(int total) => JsonSerializer.Serialize(new
    {
        type = "event_msg",
        timestamp = DateTimeOffset.Now.ToString("O"),
        payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = total, cached_input_tokens = 0, output_tokens = 0, reasoning_output_tokens = 0, total_tokens = total } } }
    });
}
