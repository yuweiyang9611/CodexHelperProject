using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageRegressionTests : IDisposable
{
    // A fixed schema 2 manifest, independent of the current backup writer and settings serializer.
    private const string Schema2Fixture = """
        {"schemaVersion":2,"createdAt":"2026-08-01T00:00:00Z","manifest":{"hashAlgorithm":"SHA-256","files":[
        {"path":"settings.json","size":17,"sha256":"db4a4b6a9f8a6b562294371d4315bb2179f3a13f36c2db630aa6268bc8ecf58c","contentBase64":"eyJ0aGVtZSI6ImxpZ2h0In0="},
        {"path":"todos.json","size":2,"sha256":"4f53cda18c2baa0c0354bb5f9a3ecbe5ed12ab4d8e11ba873c2f11161202b945","contentBase64":"W10="}]}}
        """;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexu-regression-" + Guid.NewGuid().ToString("N"));
    private CodexPaths Paths => new(_root, _root, Path.Combine(_root, "missing.sqlite"),
        Path.Combine(_root, "sessions"), Path.Combine(_root, "archive"), "missing", "missing", Path.Combine(_root, "claude"));
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public UsageRegressionTests()
    {
        Directory.CreateDirectory(Paths.SessionsDirectory);
        Directory.CreateDirectory(Path.Combine(Paths.ClaudeDirectory, "projects"));
    }
    private string Source => Path.Combine(Paths.ClaudeDirectory, "projects", "a.jsonl");
    private string Line => JsonSerializer.Serialize(new
    {
        type = "assistant",
        sessionId = "a",
        cwd = _root,
        timestamp = DateTimeOffset.Now.ToString("O"),
        message = new { model = "test", usage = new { input_tokens = 100 } }
    }) + "\n";

    [Fact]
    public async Task Claude_PeriodicValidationDetectsPreservedMetadata_AndKeepsConflictSeparate()
    {
        var clock = new Clock();
        var original = Line;
        await File.WriteAllTextAsync(Source, original);
        ClaudeCodeUsageReader.ReadMetrics? metric = null;
        ClaudeCodeUsageReader Reader(string? scope = null) => new(Paths, defaultWorkspace: scope, applicationDataDirectory: _root)
        { Clock = clock, MetricsObserved = m => metric = m };
        var first = await Reader().ReadAsync();
        var stamp = File.GetLastWriteTimeUtc(Source);
        await File.WriteAllTextAsync(Source, original.Replace("100", "200"));
        File.SetLastWriteTimeUtc(Source, stamp);
        clock.Now += TimeSpan.FromMinutes(59);
        var fast = await Reader(_root).ReadAsync();
        Assert.Equal(100, fast.Tokens.Lifetime.Tokens);
        Assert.Equal(0, metric!.TranscriptBytes);
        Assert.Equal(first.IndexStatus.LastFullValidationAt, fast.IndexStatus.LastFullValidationAt);
        clock.Now += TimeSpan.FromMinutes(1);
        var verified = await Reader().ReadAsync();
        Assert.Equal(100, verified.Tokens.Lifetime.Tokens);
        Assert.Equal(1, verified.History!.Conflicts);
        Assert.DoesNotContain(verified.Diagnostics, d => d.Contains("文件读取失败"));
        Assert.Equal(clock.Now, verified.IndexStatus.LastFullValidationAt);
        Assert.True(metric!.TranscriptBytes > 0);
        UsageReadContext.Invalidate(_root); // A new process has no successful validation time.
        Assert.Equal(1, (await Reader().ReadAsync()).History!.Conflicts);
        Assert.True(metric!.TranscriptBytes > 0);
    }

    [Fact]
    public async Task ValidationFailureStaysDue_AndRebuildForcesRead()
    {
        await File.WriteAllTextAsync(Source, Line);
        var clock = new Clock();
        var reader = new ClaudeCodeUsageReader(Paths, applicationDataDirectory: _root) { Clock = clock };
        var first = await reader.ReadAsync();
        clock.Now += TimeSpan.FromHours(1);
        using (var locked = new FileStream(Source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failed = await reader.ReadAsync();
            Assert.Equal(100, failed.Tokens.Lifetime.Tokens);
            Assert.True(failed.IndexStatus.FullValidationDue);
            Assert.Equal(first.IndexStatus.LastFullValidationAt, failed.IndexStatus.LastFullValidationAt);
            Assert.Equal(0, failed.History!.Conflicts);
            Assert.Contains(failed.Diagnostics, d => d.Contains("文件读取失败"));
        }
        Assert.False((await reader.ReadAsync()).IndexStatus.FullValidationDue);
        var manager = new LocalDataManagementService(new(_root), new(_root), _root);
        await manager.RebuildSessionIndexAsync();
        Assert.Equal(1, (await reader.ReadAsync()).IndexStatus.ParsedFiles);
    }

    [Fact]
    public async Task CancelledValidationDoesNotAdvance_AndRuntimeSchedulesAreIndependent()
    {
        await File.WriteAllTextAsync(Source, Line);
        var clock = new Clock();
        var reader = new ClaudeCodeUsageReader(Paths, applicationDataDirectory: _root) { Clock = clock };
        var first = await reader.ReadAsync();
        clock.Now += TimeSpan.FromHours(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        var schedule = UsageReadContext.For(_root).Get("validate/claude/" + Path.GetFullPath(Paths.ClaudeDirectory),
            () => new UsageReadContext.ValidationSchedule());
        Assert.Equal(first.IndexStatus.LastFullValidationAt, schedule.LastSuccess);
        var codex = await new CodexSessionReader(Paths, indexDirectory: _root) { Clock = clock }.ReadAsync();
        Assert.Equal(clock.Now, codex.IndexStatus.LastFullValidationAt);
        Assert.Equal(first.IndexStatus.LastFullValidationAt, schedule.LastSuccess);
        Assert.False((await reader.ReadAsync()).IndexStatus.FullValidationDue);
    }

    [Fact]
    public async Task Codex_PeriodicValidationDetectsEqualLengthRewrite()
    {
        var clock = new Clock();
        var file = Path.Combine(Paths.SessionsDirectory, "rollout-a.jsonl");
        var text = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "a", cwd = _root } }) + "\n"
            + JsonSerializer.Serialize(new
            {
                type = "event_msg",
                timestamp = DateTimeOffset.Now.ToString("O"),
                payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 100, total_tokens = 100 } } }
            }) + "\n";
        await File.WriteAllTextAsync(file, text);
        var reader = new CodexSessionReader(Paths, indexDirectory: _root, historyEnabled: true) { Clock = clock };
        Assert.Equal(100, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
        var context = UsageReadContext.For(_root);
        var writes = context.LedgerWrites;
        var deserialized = context.LedgerDeserialized;
        await new CodexSessionReader(Paths, indexDirectory: _root, historyEnabled: true, defaultWorkspace: _root) { Clock = clock }.ReadAsync();
        Assert.Equal(writes, context.LedgerWrites);
        Assert.Equal(deserialized, context.LedgerDeserialized);
        var stamp = File.GetLastWriteTimeUtc(file);
        await File.WriteAllTextAsync(file, text.Replace("100", "200"));
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.Equal(0, (await reader.ReadAsync()).History!.Conflicts);
        clock.Now += TimeSpan.FromHours(1);
        var verified = await reader.ReadAsync();
        Assert.Equal(100, verified.Tokens.Lifetime.Tokens);
        Assert.Equal(1, verified.History!.Conflicts);
        Assert.Equal(1, verified.IndexStatus.ParsedFiles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSchema2PreservesLedger_AcrossCommitAndRollback(bool rollback)
    {
        await File.WriteAllTextAsync(Source, Line);
        var reader = new ClaudeCodeUsageReader(Paths, applicationDataDirectory: _root);
        await reader.ReadAsync();
        File.Delete(Source);
        var ledger = Path.Combine(_root, UsageHistoryLedger.RelativePath);
        var original = await File.ReadAllBytesAsync(ledger);
        var backup = Path.Combine(_root, "schema2.json");
        await File.WriteAllTextAsync(backup, Schema2Fixture);
        var manager = new LocalDataManagementService(new(_root), new(_root), _root);
        await using (var transaction = await manager.BeginRestoreAsync(backup))
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(ledger));
            if (rollback) await transaction.RollbackAsync(); else await transaction.CommitAsync();
        }
        UsageReadContext.Invalidate(_root);
        Assert.Equal(100, (await reader.ReadAsync()).Tokens.Lifetime.Tokens);
    }

    private sealed record Marker(int Value);
    [Fact]
    public async Task ReplacedLedgerInvalidatesCachedSources()
    {
        var ledger = new UsageHistoryLedger(_root);
        await ledger.MergeAsync("test", new Dictionary<string, Marker> { ["old"] = new(100) }, (_, _) => true, default);
        var replacement = Path.Combine(_root, "replacement");
        await new UsageHistoryLedger(replacement).MergeAsync("test", new Dictionary<string, Marker> { ["new"] = new(200) }, (_, _) => true, default);
        File.Move(Path.Combine(replacement, UsageHistoryLedger.RelativePath), Path.Combine(_root, UsageHistoryLedger.RelativePath), true);
        var read = await ledger.MergeAsync("test", new Dictionary<string, Marker>(), (_, _) => true, default);
        Assert.Equal(200, Assert.Single(read.Sources).Value.Value);
        Assert.DoesNotContain("old", read.Sources.Keys);
        UsageReadContext.Invalidate(replacement);
    }

    [Fact]
    public async Task DamagedIndexReparsesInsteadOfReusingMemory()
    {
        await File.WriteAllTextAsync(Source, Line);
        var reader = new ClaudeCodeUsageReader(Paths, applicationDataDirectory: _root);
        await reader.ReadAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "claude-session-index-v1.json"), "{broken");
        var snapshot = await reader.ReadAsync();
        Assert.Equal(1, snapshot.IndexStatus.ParsedFiles);
        Assert.Equal(100, snapshot.Tokens.Lifetime.Tokens);
        Assert.Contains(snapshot.Diagnostics, d => d.Contains("索引不可用"));
    }

    public void Dispose() { UsageReadContext.Invalidate(_root); Directory.Delete(_root, true); }
}
