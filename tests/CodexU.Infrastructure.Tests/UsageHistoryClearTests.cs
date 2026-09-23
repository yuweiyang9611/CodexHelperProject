using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageHistoryClearTests
{
    [Fact]
    public async Task Clear_RemovesBothHistoriesAndLedger_WithoutTouchingSettingsSourcesOrIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-clear-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DailyUsageHistoryStore(root);
            var date = DateOnly.FromDateTime(DateTime.Today);
            foreach (var runtime in Enum.GetValues<AgentRuntime>())
                await store.SaveAsync(runtime, [new(date, new(100, 0, 0, 0, 100), 1, 0, DataQuality.Detailed)], "all");
            await new UsageHistoryLedger(root).MergeAsync("test", new Dictionary<string, int> { ["retained"] = 100 }, (_, _) => true, default);
            var settings = new AppSettingsStore(root);
            await settings.SaveAsync(new AppSettings());
            var settingsBefore = await File.ReadAllBytesAsync(Path.Combine(root, "settings.json"));
            await File.WriteAllTextAsync(Path.Combine(root, "session-index-v1.json"), "index");
            await File.WriteAllTextAsync(Path.Combine(root, "source.jsonl"), "original");

            var result = await new LocalDataManagementService(settings, new TodoStore(root), root).ClearUsageHistoryAsync();

            Assert.True(result.Success);
            foreach (var runtime in Enum.GetValues<AgentRuntime>()) Assert.Empty(await store.LoadAsync(runtime, "all"));
            Assert.False(File.Exists(Path.Combine(root, UsageHistoryLedger.RelativePath)));
            Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(Path.Combine(root, "settings.json")));
            Assert.Equal("index", await File.ReadAllTextAsync(Path.Combine(root, "session-index-v1.json")));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(root, "source.jsonl")));
            Assert.False(File.Exists(Path.Combine(root, LocalRestoreJournal.JournalFileName)));
            var fresh = await new UsageHistoryLedger(root).MergeAsync("test", new Dictionary<string, int>(), (_, _) => true, default);
            Assert.Empty(fresh.Sources);
        }
        finally { UsageReadContext.Invalidate(root); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RebuildIndex_PreservesAuthoritativeHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-rebuild-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await new UsageHistoryLedger(root).MergeAsync("test", new Dictionary<string, int> { ["deleted-source"] = 100 }, (_, _) => true, default);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, UsageHistoryLedger.RelativePath));
            await File.WriteAllTextAsync(Path.Combine(root, "session-index-v1.json"), "index");
            await new LocalDataManagementService(new AppSettingsStore(root), new TodoStore(root), root).RebuildSessionIndexAsync();
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, UsageHistoryLedger.RelativePath)));
        }
        finally { UsageReadContext.Invalidate(root); Directory.Delete(root, true); }
    }
}
