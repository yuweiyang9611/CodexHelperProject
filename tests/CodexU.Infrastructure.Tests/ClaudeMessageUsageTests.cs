using System.Text.Json;
using System.Text.Json.Nodes;
using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeMessageUsageTests
{
    [Fact]
    public async Task ContentBlocks_CountOneResponseAndKeepEveryToolAcrossIncrementalReads()
    {
        using var fixture = new Fixture();
        await File.WriteAllLinesAsync(fixture.Transcript,
        [fixture.Line("msg-one", "Read"), fixture.Line("msg-one", "Bash")]);

        var initial = await fixture.Reader().ReadAsync();
        Assert.Equal(170, initial.Tokens.Lifetime.Tokens);
        Assert.Equal(1, Assert.Single(initial.Models).EventCount);
        Assert.Equal(2, initial.Tools.Sum(tool => tool.Count));
        Assert.Equal(20, initial.Tokens.Lifetime.Breakdown.CacheWrite5mTokens);
        Assert.Equal(30, initial.Tokens.Lifetime.Breakdown.CachedInputTokens);

        // A later block repeats the response usage. Another response with the same
        // counters is real additional usage and must not be fingerprint-deduplicated.
        await File.AppendAllLinesAsync(fixture.Transcript,
        [fixture.Line("msg-one", "Grep"), fixture.Line("msg-two", "Read")]);
        var appended = await fixture.Reader().ReadAsync();
        Assert.Equal(340, appended.Tokens.Lifetime.Tokens);
        Assert.Equal(2, Assert.Single(appended.Models).EventCount);
        Assert.Equal(4, appended.Tools.Sum(tool => tool.Count));
        Assert.Equal(1, appended.IndexStatus.IncrementalFiles);
        Assert.Equal(0, appended.History!.Conflicts);

        UsageReadContext.Invalidate(fixture.Root);
        var reloaded = await fixture.Reader().ReadAsync();
        Assert.Equal(340, reloaded.Tokens.Lifetime.Tokens);
        Assert.Equal(1, reloaded.IndexStatus.ReusedFiles);
        File.Delete(fixture.Transcript);
        Assert.Equal(340, (await fixture.Reader().ReadAsync()).Tokens.Lifetime.Tokens);
    }

    [Fact]
    public async Task UsageWithoutMessageIdsRemainsIndependentAndLaterCompleteUsageWins()
    {
        using var fixture = new Fixture();
        await File.WriteAllLinesAsync(fixture.Transcript,
        [
            fixture.Line(null, "Read"), fixture.Line(null, "Read"),
            fixture.Line("streamed", "Read", output: 10), fixture.Line("streamed", "Bash", output: 40),
            fixture.Line("streamed", "Grep", output: 10)
        ]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(530, result.Tokens.Lifetime.Tokens);
        Assert.Equal(3, Assert.Single(result.Models).EventCount);
        Assert.Equal(5, result.Tools.Sum(tool => tool.Count));
    }

    [Fact]
    public async Task RepeatedBlockCanEnrichCacheTtlWithoutBeingChargedTwice()
    {
        using var fixture = new Fixture();
        var flat = fixture.Line("msg-one", "Read");
        var detailed = JsonNode.Parse(fixture.Line("msg-one", "Bash"))!;
        detailed["message"]!["usage"]!["cache_creation"] = new JsonObject
        {
            ["ephemeral_5m_input_tokens"] = 0,
            ["ephemeral_1h_input_tokens"] = 20
        };
        await File.WriteAllLinesAsync(fixture.Transcript, [flat, detailed.ToJsonString(), flat]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(170, result.Tokens.Lifetime.Tokens);
        Assert.Equal(0, result.Tokens.Lifetime.Breakdown.CacheWrite5mTokens);
        Assert.Equal(20, result.Tokens.Lifetime.Breakdown.CacheWrite1hTokens);
        Assert.NotNull(Assert.Single(result.AnalysisData!.Entries).CreditsUsed);
    }

    [Fact]
    public async Task OldLedgerIsEnrichedAndCorrectedWhenOriginalTranscriptReturns()
    {
        using var fixture = new Fixture();
        var lines = new[] { fixture.Line("msg-one", "Read"), fixture.Line("msg-one", "Bash") };
        await File.WriteAllLinesAsync(fixture.Transcript, lines);
        await fixture.Reader().ReadAsync();

        // Recreate the old persisted shape: raw per-block counters, no message ID.
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(fixture.Root, UsageHistoryLedger.RelativePath), Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            using var read = db.CreateCommand();
            read.CommandText = "SELECT payload FROM sources WHERE runtime='claude'";
            var payload = JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!.AsObject();
            payload.Remove("parserVersion");
            foreach (var item in payload["events"]!.AsArray()) item!.AsObject().Remove("messageId");
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE sources SET payload=$payload WHERE runtime='claude'";
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            await update.ExecuteNonQueryAsync();
        }
        var indexPath = Path.Combine(fixture.Root, "claude-session-index-v1.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!;
        index["version"] = 4;
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());
        UsageReadContext.Invalidate(fixture.Root);
        File.Delete(fixture.Transcript);

        var retained = await fixture.Reader().ReadAsync();
        Assert.Equal(340, retained.Tokens.Lifetime.Tokens);
        Assert.Contains(retained.Diagnostics, item => item.Contains("缺少消息标识", StringComparison.Ordinal));

        await File.WriteAllLinesAsync(fixture.Transcript, lines);
        var corrected = await fixture.Reader().ReadAsync();
        Assert.Equal(170, corrected.Tokens.Lifetime.Tokens);
        Assert.Equal(0, corrected.History!.Conflicts);
        Assert.DoesNotContain(corrected.Diagnostics, item => item.Contains("缺少消息标识", StringComparison.Ordinal));

        File.Delete(fixture.Transcript);
        UsageReadContext.Invalidate(fixture.Root);
        var replayed = await fixture.Reader().ReadAsync();
        Assert.Equal(170, replayed.Tokens.Lifetime.Tokens);
        Assert.Equal(1, replayed.History!.RetainedSources);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-claude-message-" + Guid.NewGuid().ToString("N"));
        internal string Transcript { get; }
        private readonly DateTimeOffset _timestamp = DateTimeOffset.Now;
        internal Fixture()
        {
            Transcript = Path.Combine(Root, ".claude", "projects", "sample", "session.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(Transcript)!);
        }
        internal ClaudeCodeUsageReader Reader() => new(new CodexPaths(Root, Root, Path.Combine(Root, "missing.sqlite"),
            Root, Root, Root, Path.Combine(Root, "missing-goals.sqlite"), Path.Combine(Root, ".claude")), applicationDataDirectory: Root);
        internal string Line(string? id, string tool, long output = 20) => JsonSerializer.Serialize(new
        {
            type = "assistant",
            sessionId = "session-one",
            cwd = Root,
            timestamp = _timestamp,
            message = new
            {
                id,
                model = "claude-opus-5-5",
                usage = new { input_tokens = 100, cache_creation_input_tokens = 20, cache_read_input_tokens = 30, output_tokens = output },
                content = new[] { new { type = "tool_use", name = tool, input = new { } } }
            }
        });
        public void Dispose()
        {
            UsageReadContext.Invalidate(Root);
            Directory.Delete(Root, recursive: true);
        }
    }
}
