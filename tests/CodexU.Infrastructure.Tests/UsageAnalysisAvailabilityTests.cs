using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageAnalysisAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexu-analysis-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private string Sessions => Path.Combine(_root, "sessions");
    private static readonly DateOnly Day = new(2025, 1, 10);
    private static readonly ModelCreditRate Rate = new("model-a", 100, 10, 200, new(2020, 1, 1));

    [Fact]
    public async Task Codex_OldRetainedAvailabilityIsUnknown_AndOriginalLogBackfillsWithoutConflict()
    {
        Directory.CreateDirectory(Sessions);
        var file = Path.Combine(Sessions, "rollout-a.jsonl");
        var content = CodexLog("session", _root, 100);
        await File.WriteAllTextAsync(file, content);
        var parsed = await CodexSessionReader.ParseFileAsync(file);
        var old = parsed with
        {
            TokenEvents = parsed.TokenEvents.Select(e => e with { AvailableFields = null }).ToArray(),
            UsageBuckets = parsed.UsageBuckets.Select(e => e with { AvailableFields = null }).ToArray()
        };
        await new UsageHistoryLedger(Data).MergeAsync("codex", new Dictionary<string, ParsedSessionFile> { ["session"] = old }, (_, _) => true, default);
        File.Delete(file);
        var reader = new CodexSessionReader(CodexPaths.Discover(_root), indexDirectory: Data, customRates: [Rate], historyEnabled: true);
        var retained = await reader.ReadAsync();
        var unknown = Assert.Single(retained.AnalysisData!.Entries);
        Assert.Equal(100, unknown.Tokens);
        Assert.Empty(unknown.AvailableBreakdownFields!);
        Assert.Null(unknown.CreditsUsed);
        Assert.Equal(100, retained.Tokens.Lifetime.IncompleteBreakdownTokens);
        Assert.Equal(0, retained.Tokens.Lifetime.CreditsUsed);
        Assert.NotNull(unknown.Rate);
        Assert.Empty(UsageAnalysisQuery.Execute(retained.AnalysisData, new()).MissingRates!);

        await File.WriteAllTextAsync(file, content);
        var backfilled = await reader.ReadAsync();
        var entry = Assert.Single(backfilled.AnalysisData!.Entries);
        Assert.Contains("inputTokens", entry.AvailableBreakdownFields!);
        Assert.Contains("cachedInputTokens", entry.AvailableBreakdownFields!);
        Assert.Contains("outputTokens", entry.AvailableBreakdownFields!);
        Assert.DoesNotContain("reasoningOutputTokens", entry.AvailableBreakdownFields!);
        Assert.DoesNotContain("cacheWrite5mTokens", entry.AvailableBreakdownFields!);
        Assert.Equal(0.01, entry.CreditsUsed!.Value, 8);
        Assert.Equal(entry.CreditsUsed, backfilled.Tokens.Lifetime.CreditsUsed);
        Assert.Equal(0, backfilled.History!.Conflicts);
        File.Delete(file);
        Assert.Equal(entry.AvailableBreakdownFields, Assert.Single((await reader.ReadAsync()).AnalysisData!.Entries).AvailableBreakdownFields);
    }

    [Fact]
    public async Task Codex_TotalOnlyAndNewlyAppearingCumulativeFields_DoNotInventZeroComponents()
    {
        Directory.CreateDirectory(Sessions);
        var file = Path.Combine(Sessions, "rollout-a.jsonl");
        var prefix = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "session", source = "cli" } }) + "\n";
        prefix += JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "model-a" } }) + "\n";
        var first = JsonSerializer.Serialize(new { timestamp = "2025-01-10T12:00:00+09:00", type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { total_tokens = 100 } } } });
        var second = JsonSerializer.Serialize(new { timestamp = "2025-01-11T12:00:00+09:00", type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 150, output_tokens = 0, total_tokens = 150 } } } });
        await File.WriteAllTextAsync(file, prefix + first + "\n" + second + "\n");
        var parsed = await CodexSessionReader.ParseFileAsync(file);
        Assert.Equal(2, parsed.TokenEvents.Count);
        Assert.All(parsed.TokenEvents, e => Assert.Equal(UsageBreakdownFields.None, e.AvailableFields));
        var snapshot = await new CodexSessionReader(CodexPaths.Discover(_root), indexDirectory: Data, customRates: [Rate], historyEnabled: true).ReadAsync();
        Assert.Equal(150, snapshot.AnalysisData!.Entries.Sum(e => e.Tokens));
        Assert.All(snapshot.AnalysisData.Entries, e => Assert.Null(e.CreditsUsed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claude_DistinguishesMissingCacheFieldsFromObservedZero(bool complete)
    {
        var project = Path.Combine(_root, "claude", "projects");
        Directory.CreateDirectory(project);
        var usage = complete ? new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 0, ["cache_read_input_tokens"] = 0, ["cache_creation_input_tokens"] = 0 }
            : new Dictionary<string, long> { ["input_tokens"] = 10, ["output_tokens"] = 0 };
        var line = JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "session",
            timestamp = "2025-01-10T12:00:00+09:00",
            cwd = _root,
            message = new { model = "model-a", usage }
        });
        await File.WriteAllTextAsync(Path.Combine(project, "a.jsonl"), line + "\n");
        var paths = CodexPaths.Discover(_root) with { ClaudeDirectory = Path.Combine(_root, "claude"), ClaudeDesktopDataDirectory = null };
        var snapshot = await new ClaudeCodeUsageReader(paths, customRates: [Rate], applicationDataDirectory: Data).ReadAsync();
        var entry = Assert.Single(snapshot.AnalysisData!.Entries);
        Assert.Equal(10, entry.Tokens);
        Assert.Contains("outputTokens", entry.AvailableBreakdownFields!);
        Assert.DoesNotContain("reasoningOutputTokens", entry.AvailableBreakdownFields!);
        Assert.Equal(complete, entry.AvailableBreakdownFields!.Contains("inputTokens"));
        Assert.Equal(complete, entry.AvailableBreakdownFields!.Contains("cacheWrite5mTokens"));
        Assert.Equal(complete, entry.CreditsUsed is not null);
        Assert.Empty(UsageAnalysisQuery.Execute(snapshot.AnalysisData, new(AgentRuntime.ClaudeCode)).MissingRates!);
    }

    [Theory]
    [InlineData(AgentRuntime.ClaudeCode)]
    [InlineData(AgentRuntime.Codex)]
    public async Task MixedCoverageOnOneDay_ReconcilesOverviewProjectDetailAndExport(AgentRuntime runtime)
    {
        var paths = CodexPaths.Discover(_root) with { ClaudeDirectory = Path.Combine(_root, "claude"), ClaudeDesktopDataDirectory = null };
        var timestamp = DateTimeOffset.Now;
        var complete = new Dictionary<string, long>
        {
            ["input_tokens"] = 100,
            ["output_tokens"] = 0,
            [runtime == AgentRuntime.ClaudeCode ? "cache_read_input_tokens" : "cached_input_tokens"] = 0
        };
        if (runtime == AgentRuntime.ClaudeCode) complete["cache_creation_input_tokens"] = 0;
        var missing = new Dictionary<string, long> { ["input_tokens"] = 40, ["output_tokens"] = 0 };
        TokenSummary tokens;
        IReadOnlyList<ProjectUsage> projects;
        UsageAnalysisData analysis;
        if (runtime == AgentRuntime.ClaudeCode)
        {
            var folder = Path.Combine(paths.ClaudeDirectory, "projects");
            Directory.CreateDirectory(folder);
            var lines = new[] { complete, missing }.Select(usage => JsonSerializer.Serialize(new
            {
                type = "assistant",
                sessionId = "example",
                timestamp,
                cwd = _root,
                message = new { model = "model-a", usage }
            }));
            await File.WriteAllLinesAsync(Path.Combine(folder, "session.jsonl"), lines);
            var snapshot = await new ClaudeCodeUsageReader(paths, customRates: [Rate], applicationDataDirectory: Data).ReadAsync();
            (tokens, projects, analysis) = (snapshot.Tokens, snapshot.Projects, snapshot.AnalysisData!);
        }
        else
        {
            Directory.CreateDirectory(Sessions);
            var lines = new[]
            {
                JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "example", cwd = _root } }),
                JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "model-a" } })
            }.Concat(new[] { complete, missing }.Select(usage => JsonSerializer.Serialize(new
            {
                timestamp,
                type = "event_msg",
                payload = new { type = "token_count", info = new { last_token_usage = usage } }
            })));
            await File.WriteAllLinesAsync(Path.Combine(Sessions, "rollout-a.jsonl"), lines);
            var snapshot = await new CodexSessionReader(paths, indexDirectory: Data, customRates: [Rate], historyEnabled: true).ReadAsync();
            (tokens, projects, analysis) = (snapshot.Tokens, snapshot.Projects, snapshot.AnalysisData!);
        }

        var detail = UsageAnalysisQuery.Execute(analysis, new(runtime));
        foreach (var period in new[] { tokens.Today, tokens.SevenDays, tokens.Month, tokens.Lifetime })
        {
            Assert.Equal(140, period.Tokens);
            Assert.Equal(0.01, period.CreditsUsed, 8);
            Assert.Equal(40, period.UnratedTokens);
            Assert.Equal(40, period.IncompleteBreakdownTokens);
            Assert.Equal(0, period.MissingRateTokens);
            Assert.Equal(100, Assert.Single(period.CreditsByModel).Tokens.VisibleTotalTokens);
        }
        Assert.Equal(tokens.Lifetime.CreditsUsed, Assert.Single(projects).CreditsUsed);
        Assert.Equal(tokens.Lifetime.CreditsUsed, detail.Totals.CreditsUsed);
        Assert.Equal(tokens.Lifetime.UnratedTokens, detail.Totals.UnratedTokens);
        Assert.Empty(detail.MissingRates!);
        var destination = Path.Combine(_root, "report.json");
        var exporter = new LocalDataManagementService(new AppSettingsStore(Data), new TodoStore(Data), Data);
        Assert.True((await exporter.ExportUsageAnalysisAsync(detail, destination, "json")).Success);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
        var exported = json.RootElement.GetProperty("totals");
        Assert.Equal(140, exported.GetProperty("tokens").GetInt64());
        Assert.Equal(40, exported.GetProperty("unratedTokens").GetInt64());
        Assert.Equal(tokens.Lifetime.CreditsUsed, exported.GetProperty("creditsUsed").GetDouble());
    }

    [Fact]
    public async Task Query_IgnoresDashboardDefaultWorkspace_AndRetainsReliableTitleBeyond182Days()
    {
        Directory.CreateDirectory(Sessions);
        var projectA = Path.Combine(_root, "project-a");
        var projectB = Path.Combine(_root, "project-b");
        var a = Path.Combine(Sessions, "rollout-a.jsonl");
        var b = Path.Combine(Sessions, "rollout-b.jsonl");
        await File.WriteAllTextAsync(a, CodexLog("session-a", projectA, 100));
        await File.WriteAllTextAsync(b, CodexLog("session-b", projectB, 200));
        var database = Path.Combine(_root, "state_5.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE threads(id TEXT, cwd TEXT, title TEXT); INSERT INTO threads VALUES ('session-a', $cwd, 'Reliable title');";
            command.Parameters.AddWithValue("$cwd", projectA);
            await command.ExecuteNonQueryAsync();
        }
        var service = DashboardService.CreateDefault(_root, defaultWorkspace: projectA, customRates: [Rate], applicationDataDirectory: Data);
        var all = await service.QueryUsageAsync(new());
        Assert.Equal(300, all.Totals.Tokens);
        Assert.Equal(Day, all.AvailableFrom);
        Assert.Equal(2, all.SessionCount);
        Assert.Equal("Reliable title", Assert.Single(all.Sessions, e => e.Id == "session-a").Title);
        Assert.Equal(100, (await service.QueryUsageAsync(new(Project: projectA))).Totals.Tokens);
        File.Delete(a);
        File.Delete(b);
        File.Delete(database);
        // Source changes become visible on the next refresh revision; paging shares the current one.
        service.InvalidateUsageAnalysis();
        var retained = await service.QueryUsageAsync(new());
        Assert.Equal(300, retained.Totals.Tokens);
        Assert.Equal("Reliable title", Assert.Single(retained.Sessions, e => e.Id == "session-a").Title);
        Assert.All(retained.Sessions.SelectMany(e => e.Members), e => Assert.Equal("retained", Assert.Single(e.Sources)));
    }

    [Fact]
    public void MissingRateSummary_CoversOffPageKnownModels_ExcludesMissingEvidenceAndUnknowns()
    {
        var entries = new[]
        {
            new AttributedUsage("a", _root, Day, "model-a", new(100, 0, 0, 0, 100), 1),
            new AttributedUsage("b", _root, Day, "model-b", new(10, 0, 0, 0, 10), 1),
            new AttributedUsage("c", _root, Day, "claude-unknown", new(5, 0, 0, 0, 5), 1)
        };
        var data = UsageHistoryProjection.BuildAnalysis(entries, [], [Rate], true);
        var result = UsageAnalysisQuery.Execute(data, new(PageSize: 1));
        Assert.Equal("a", Assert.Single(result.Sessions).Id);
        var missing = Assert.Single(result.MissingRates!);
        Assert.Equal("model-b", missing.Model);
        Assert.Equal(Day, missing.Date);
        Assert.Equal(10, missing.Tokens);
    }

    private static string CodexLog(string id, string project, int tokens) => string.Join('\n',
        JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, cwd = project, source = "cli" } }),
        JsonSerializer.Serialize(new { type = "turn_context", payload = new { model = "model-a" } }),
        JsonSerializer.Serialize(new { timestamp = "2025-01-10T12:00:00+09:00", type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = tokens, cached_input_tokens = 0, output_tokens = 0, total_tokens = tokens } } } })) + "\n";

    public void Dispose()
    {
        UsageReadContext.Invalidate(Data);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
