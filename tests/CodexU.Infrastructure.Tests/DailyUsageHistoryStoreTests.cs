using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class DailyUsageHistoryStoreTests
{
    [Fact]
    public async Task SaveAsync_PreservesRowsRecordedUnderADifferentScope()
    {
        // The severest failure mode for this store: totals depend on the workspace
        // and subagent filters active when they were computed, so rows carry a scope.
        // A rewrite that kept only the current scope would silently delete history
        // the user can never regain, just because they changed a filter.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var otherScope = DailyUsageHistoryStore.ScopeFingerprint(@"D:\other");
            var mineScope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var date = new DateOnly(2026, 7, 20);

            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 100)], otherScope);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 999)], mineScope);

            var other = Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, otherScope));
            var mine = Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, mineScope));

            Assert.Equal(100, other.Tokens.InputTokens);
            Assert.Equal(999, mine.Tokens.InputTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ReplacesRatherThanDuplicatesTheSameDay()
    {
        // A refresh re-reads today repeatedly; each pass must update the day in
        // place rather than append a new row every few minutes.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var date = new DateOnly(2026, 7, 29);

            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 10)], scope);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 20)], scope);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 30)], scope);

            var day = Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, scope));
            Assert.Equal(30, day.Tokens.InputTokens);

            var lines = await File.ReadAllLinesAsync(store.PathFor(AgentRuntime.ClaudeCode));
            Assert.Single(lines, line => !string.IsNullOrWhiteSpace(line));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_RoundTripsTheCacheWriteSplit()
    {
        // The split has to be carried from day one; adding it later would force a
        // migration of every row already written.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var tokens = new TokenBreakdown(1_000, 300, 500, 0, 1_500, CacheWrite5mTokens: 200, CacheWrite1hTokens: 100);

            await store.SaveAsync(
                AgentRuntime.ClaudeCode,
                [new DailyUsageRecord(new DateOnly(2026, 7, 25), tokens, 12.5, 7, DataQuality.Detailed)],
                scope);

            var day = Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, scope));

            Assert.Equal(200, day.Tokens.CacheWrite5mTokens);
            Assert.Equal(100, day.Tokens.CacheWrite1hTokens);
            Assert.Equal(400, day.Tokens.UncachedInputTokens);
            Assert.Equal(12.5, day.CreditsUsed);
            Assert.Equal(7, day.UnratedTokens);
            Assert.Equal(DataQuality.Detailed, day.Quality);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_SurvivesATornTrailingLine()
    {
        // A crash mid-write must cost that day's last revision, not the file.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(new DateOnly(2026, 7, 18), 42)], scope);
            await File.AppendAllTextAsync(store.PathFor(AgentRuntime.ClaudeCode), "{\"v\":1,\"date\":\"2026-07-1");

            var day = Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, scope));
            Assert.Equal(42, day.Tokens.InputTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_KeepsRuntimesInSeparateFiles()
    {
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var date = new DateOnly(2026, 7, 22);

            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(date, 11)], scope);
            await store.SaveAsync(AgentRuntime.Codex, [Day(date, 22)], scope);

            Assert.Equal(11, Assert.Single(await store.LoadAsync(AgentRuntime.ClaudeCode, scope)).Tokens.InputTokens);
            Assert.Equal(22, Assert.Single(await store.LoadAsync(AgentRuntime.Codex, scope)).Tokens.InputTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsNothingBeforeAnythingIsRecorded()
    {
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            Assert.Empty(await store.LoadAsync(
                AgentRuntime.ClaudeCode,
                DailyUsageHistoryStore.ScopeFingerprint(null)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScopeFingerprint_SeparatesFiltersThatChangeWhatIsCounted()
    {
        var all = DailyUsageHistoryStore.ScopeFingerprint(null);
        var scoped = DailyUsageHistoryStore.ScopeFingerprint(@"D:\Workspace\Repo");

        Assert.NotEqual(all, scoped);
        // Trailing separator and casing describe the same workspace.
        Assert.Equal(scoped, DailyUsageHistoryStore.ScopeFingerprint(@"d:\workspace\repo\"));
    }

    [Fact]
    public async Task LoadAsync_ReclaimsDaysRecordedUnderTheLegacySubagentScope()
    {
        // The fingerprint used to fold in the show-subagents preference, which changes
        // the task board and not one number in this store. Toggling that checkbox forked
        // the history and made every earlier day unreadable — for a store that exists to
        // remember days whose transcripts have rotated away, that is the only surviving
        // copy. Both legacy variants map back onto the workspace they always described.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(new(2026, 7, 10), 10)], $"{scope}|subagents=0");
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(new(2026, 7, 11), 20)], $"{scope}|subagents=1");

            var loaded = await store.LoadAsync(AgentRuntime.ClaudeCode, scope);

            Assert.Equal(2, loaded.Count);
            Assert.Equal([10, 20], loaded.Select(record => record.Tokens.TotalTokens));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ReplacesALegacyRowRatherThanLeavingItBeside()
    {
        // A legacy row for a date being rewritten has to go, or the file grows a stale
        // duplicate on every refresh for as long as that day stays in range.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var scope = DailyUsageHistoryStore.ScopeFingerprint(null);
            var day = new DateOnly(2026, 7, 10);
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(day, 10)], $"{scope}|subagents=1");

            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(day, 99)], scope);

            var loaded = await store.LoadAsync(AgentRuntime.ClaudeCode, scope);
            Assert.Single(loaded);
            Assert.Equal(99, loaded[0].Tokens.TotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_StillIgnoresAGenuinelyDifferentWorkspace()
    {
        // Reclaiming the legacy tail must not turn into matching everything.
        var root = CreateRoot();
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var mine = DailyUsageHistoryStore.ScopeFingerprint(@"D:\Mine");
            var theirs = DailyUsageHistoryStore.ScopeFingerprint(@"D:\Theirs");
            await store.SaveAsync(AgentRuntime.ClaudeCode, [Day(new(2026, 7, 10), 10)], $"{theirs}|subagents=0");

            Assert.Empty(await store.LoadAsync(AgentRuntime.ClaudeCode, mine));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    private static DailyUsageRecord Day(DateOnly date, long inputTokens) => new(
        date,
        new TokenBreakdown(inputTokens, 0, 0, 0, inputTokens),
        0,
        0,
        DataQuality.Detailed);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
