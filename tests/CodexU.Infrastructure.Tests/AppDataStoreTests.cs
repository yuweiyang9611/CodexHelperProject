using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class AppDataStoreTests
{
    [Fact]
    public async Task SettingsStore_NewInstallReturnsNormalizedCollections()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-new-settings-{Guid.NewGuid():N}");
        try
        {
            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.NotNull(result.CustomModelRates);
            Assert.Empty(result.CustomModelRates);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_NormalizesAndPersistsValues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 120, FiveHourAlertPercent: 0));

            var result = await store.LoadAsync();

            Assert.Equal(60, result.AutoRefreshMinutes);
            Assert.Equal(1, result.FiveHourAlertPercent);
            Assert.Equal(110, result.UiScalePercent);
            Assert.Equal(40, result.AmountPerThousandCredits);
            Assert.Equal("US$", result.CreditCurrencySymbol);
            Assert.Equal(200, result.CodexMonthlySubscriptionAmount);
            Assert.Equal(20, result.ClaudeMonthlySubscriptionAmount);
            Assert.True(result.CodexAutoDetectSubscriptionAmount);
            Assert.True(result.CloseToTray);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_PersistsCloseToTrayPreference()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-close-behavior-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);

            await store.SaveAsync(new AppSettings(CloseToTray: false));

            Assert.False((await store.LoadAsync()).CloseToTray);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_PersistsStatusStripPositionLock()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-status-strip-lock-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);

            await store.SaveAsync(new AppSettings(StatusStripPositionLocked: true));

            Assert.True((await store.LoadAsync()).StatusStripPositionLocked);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_FirstSaveSucceedsWhenInitialBackupCannotBeCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-settings-blocked-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "settings.json.bak"));

        try
        {
            var store = new AppSettingsStore(root);

            var saved = await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 17));

            Assert.Equal(17, saved.AutoRefreshMinutes);
            Assert.Equal(17, (await store.LoadAsync()).AutoRefreshMinutes);
            Assert.True(File.Exists(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_AlwaysNormalizesEquivalentAmountsToUsDollars()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-credit-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(
                AmountPerThousandCredits: 280,
                CreditCurrencySymbol: "¥",
                CodexMonthlySubscriptionAmount: 168,
                CodexAutoDetectSubscriptionAmount: false));

            var result = await store.LoadAsync();

            Assert.Equal(280, result.AmountPerThousandCredits);
            Assert.Equal("US$", result.CreditCurrencySymbol);
            Assert.Equal(168, result.CodexMonthlySubscriptionAmount);
            Assert.False(result.CodexAutoDetectSubscriptionAmount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_MovesALegacySubscriptionAmountOntoCodex()
    {
        // The single field defaulted to 200, a ChatGPT price, so whatever a user typed
        // there described Codex. Dropping it would silently reset their manual value to
        // the default on the first launch after upgrading.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-legacy-subscription-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"theme":"dark","monthlySubscriptionAmount":168,"autoDetectSubscriptionAmount":false}""");

            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.Equal(168, result.CodexMonthlySubscriptionAmount);
            // Never onto Claude: the legacy value was chosen against a ChatGPT price.
            Assert.Equal(20, result.ClaudeMonthlySubscriptionAmount);
            // The auto-detect flag goes onto BOTH, unlike the amount — it expressed one
            // preference about both runtimes, so honouring it on only one would silently
            // re-enable auto-detection the user had turned off.
            Assert.False(result.CodexAutoDetectSubscriptionAmount);
            Assert.False(result.ClaudeAutoDetectSubscriptionAmount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_PrefersAnExplicitCodexAmountOverTheLegacyField()
    {
        // A file written by this version carries both keys, because the record still
        // serializes everything. The explicit one is authoritative.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-both-subscription-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"monthlySubscriptionAmount":168,"codexMonthlySubscriptionAmount":42,"claudeMonthlySubscriptionAmount":100}""");

            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.Equal(42, result.CodexMonthlySubscriptionAmount);
            Assert.Equal(100, result.ClaudeMonthlySubscriptionAmount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_UsesDefaultSubscriptionForLegacySettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-legacy-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """{"theme":"dark","amountPerThousandCredits":40,"creditCurrencySymbol":"US$"}""");

            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.Equal(200, result.CodexMonthlySubscriptionAmount);
            Assert.Equal(20, result.ClaudeMonthlySubscriptionAmount);
            Assert.True(result.CodexAutoDetectSubscriptionAmount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_RejectsInvalidPathBeforeWritingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-invalid-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SaveAsync(new AppSettings(CodexHome: "\"")));

            Assert.False(File.Exists(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_RecoversBackupAndPreservesCorruptPrimary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-settings-backup-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 7));
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 12));
            await File.WriteAllTextAsync(store.SettingsPath, "{ broken");

            var recovered = await store.LoadAsync();

            Assert.Equal(7, recovered.AutoRefreshMinutes);
            Assert.Single(Directory.GetFiles(root, "settings.corrupt-*.json"));
            Assert.Equal(7, (await new AppSettingsStore(root).LoadAsync()).AutoRefreshMinutes);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_PersistsPinnedRateCatalogMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-pinned-rates-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(
                CustomModelRates: [new ModelCreditRate("archive-model", 10, 1, 100)],
                IsRateCatalogPinned: true,
                PinnedRateCatalogVersion: " archive-v1 ",
                PinnedRateCatalogSource: " archived vendor table ",
                PinnedRateCatalogBaseVersion: " vendor-base-v1 "));

            var result = await store.LoadAsync();

            Assert.True(result.IsRateCatalogPinned);
            Assert.Equal("archive-v1", result.PinnedRateCatalogVersion);
            Assert.Equal("archived vendor table", result.PinnedRateCatalogSource);
            Assert.Equal("vendor-base-v1", result.PinnedRateCatalogBaseVersion);
            Assert.Single(result.CustomModelRates!);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_ReturnsValidBackupWhenPrimaryRepairIsTemporarilyBlocked()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-settings-locked-primary-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 7));
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 12));
            await File.WriteAllTextAsync(store.SettingsPath, "{ broken");

            await using var primaryLock = new FileStream(
                store.SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var recovered = await store.LoadAsync();

            Assert.Equal(7, recovered.AutoRefreshMinutes);
            Assert.Single(Directory.GetFiles(root, "settings.corrupt-*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_PreservesLegacyPrefixMatchingWhenMatchModeWasNotPersisted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-legacy-rate-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "settings.json"),
                """
                {
                  "customModelRates": [
                    {
                      "model": "legacy-family",
                      "inputCreditsPerMillion": 1,
                      "cachedInputCreditsPerMillion": 0.1,
                      "outputCreditsPerMillion": 2
                    },
                    {
                      "model": "new-exact",
                      "inputCreditsPerMillion": 3,
                      "cachedInputCreditsPerMillion": 0.3,
                      "outputCreditsPerMillion": 4,
                      "matchMode": "exact"
                    }
                  ]
                }
                """);

            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.Collection(
                result.CustomModelRates!,
                legacy =>
                {
                    Assert.Equal("legacy-family", legacy.Model);
                    Assert.Equal("prefix", legacy.MatchMode);
                },
                current =>
                {
                    Assert.Equal("new-exact", current.Model);
                    Assert.Equal("exact", current.MatchMode);
                });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_DoubleCorruptionFallsBackToNormalizedDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-corrupt-settings-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{ broken");
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json.bak"), "{ also-broken");

            var result = await new AppSettingsStore(root).LoadAsync();

            Assert.NotNull(result.CustomModelRates);
            Assert.Empty(result.CustomModelRates);
            Assert.Equal(110, result.UiScalePercent);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_RecoversBackupWhenPinnedPrimaryIsSemanticallyInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-invalid-pinned-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            await store.SaveAsync(new AppSettings(AutoRefreshMinutes: 9));
            await File.WriteAllTextAsync(
                store.SettingsPath,
                """
                {
                  "autoRefreshMinutes": 42,
                  "isRateCatalogPinned": true,
                  "pinnedRateCatalogVersion": "broken",
                  "pinnedRateCatalogSource": "broken",
                  "customModelRates": []
                }
                """);

            var recovered = await store.LoadAsync();

            Assert.Equal(9, recovered.AutoRefreshMinutes);
            Assert.False(recovered.IsRateCatalogPinned);
            Assert.Single(Directory.GetFiles(root, "settings.corrupt-*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_RejectsAliasesWithTheSameEffectiveDateBeforeNormalizing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-duplicate-rates-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            var effectiveFrom = new DateOnly(2026, 7, 14);

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new AppSettings(
                CustomModelRates:
                [
                    new ModelCreditRate("gpt-5.2", 1, 1, 1, effectiveFrom, MatchMode: "exact"),
                    new ModelCreditRate("gpt-5.2-codex", 2, 2, 2, effectiveFrom, MatchMode: "exact")
                ])));

            Assert.Contains("重复", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SettingsStore_RejectsMoreThanTheMaximumCustomRatesInsteadOfTruncating()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-too-many-rates-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            var rates = Enumerable.Range(0, UsageCredits.MaximumCustomRateCount + 1)
                .Select(index => new ModelCreditRate($"custom-{index}", 1, 1, 1, MatchMode: "exact"))
                .ToArray();

            var exception = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                new AppSettings(CustomModelRates: rates)));

            Assert.Contains(UsageCredits.MaximumCustomRateCount.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(-1d, "exact")]
    [InlineData(1_000_001d, "exact")]
    [InlineData(1d, "family")]
    public async Task SettingsStore_RejectsInvalidCustomRateFields(double inputRate, string matchMode)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-invalid-rate-{Guid.NewGuid():N}");
        try
        {
            var store = new AppSettingsStore(root);
            var rate = new ModelCreditRate(
                "custom-model",
                inputRate,
                1,
                1,
                MatchMode: matchMode);

            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                new AppSettings(CustomModelRates: [rate])));

            Assert.False(File.Exists(store.SettingsPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TodoStore_SupportsAddToggleUpdateDeleteAndClear()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-todos-{Guid.NewGuid():N}");
        try
        {
            var store = new TodoStore(root);
            var items = await store.AddAsync(new TodoMutation(null, "完成界面", "high", DateOnly.FromDateTime(DateTime.Today), "thread-1"));
            var item = Assert.Single(items);
            Assert.Equal("high", item.Priority);

            items = await store.ToggleAsync(item.Id);
            Assert.True(Assert.Single(items).Done);

            items = await store.UpdateAsync(new TodoMutation(item.Id, "完成全部界面", "normal", null, item.ThreadId));
            Assert.Equal("完成全部界面", Assert.Single(items).Text);

            items = await store.ClearCompletedAsync();
            Assert.Empty(items);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TodoStore_FirstMutationSucceedsWhenInitialBackupCannotBeCreated()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-todos-blocked-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "todos.json.bak"));

        try
        {
            var store = new TodoStore(root);

            var items = await store.AddAsync(
                new TodoMutation(null, "first durable todo", "normal", null, null));

            Assert.Equal("first durable todo", Assert.Single(items).Text);
            Assert.Equal("first durable todo", Assert.Single(await store.ListAsync()).Text);
            Assert.True(File.Exists(Path.Combine(root, "todos.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TodoStore_CorruptFileBlocksMutationWithoutOverwritingOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-corrupt-todos-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "todos.json");
        const string corruptContent = "{ this is not valid json";
        await File.WriteAllTextAsync(path, corruptContent);

        try
        {
            var store = new TodoStore(root);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.AddAsync(new TodoMutation(null, "must not overwrite", "normal", null, null)));

            Assert.Contains("已停止写入", exception.Message, StringComparison.Ordinal);
            Assert.Equal(corruptContent, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TodoStore_RecoversFromBackupAndPreservesCorruptPrimary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-backup-todos-{Guid.NewGuid():N}");
        try
        {
            var store = new TodoStore(root);
            _ = await store.AddAsync(new TodoMutation(null, "first", "normal", null, null));
            _ = await store.AddAsync(new TodoMutation(null, "second", "normal", null, null));
            await File.WriteAllTextAsync(Path.Combine(root, "todos.json"), "{ broken");

            var recovered = await store.ListAsync();
            Assert.Equal("first", Assert.Single(recovered).Text);

            var saved = await store.AddAsync(new TodoMutation(null, "after recovery", "high", null, null));
            Assert.Equal(2, saved.Count);
            Assert.Equal(2, (await store.ListAsync()).Count);
            Assert.Single(Directory.GetFiles(root, "todos.corrupt-*.json"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    public async Task TodoStore_RecoversSemanticInvalidPrimaryFromBackup(string invalidJson)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-semantic-invalid-todos-{Guid.NewGuid():N}");
        try
        {
            var store = new TodoStore(root);
            _ = await store.AddAsync(new TodoMutation(null, "first", "normal", null, null));
            _ = await store.AddAsync(new TodoMutation(null, "second", "normal", null, null));
            await File.WriteAllTextAsync(Path.Combine(root, "todos.json"), invalidJson);

            var recovered = await store.ListAsync();

            Assert.Equal("first", Assert.Single(recovered).Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TodoStore_RejectsItemsBeyondMaximumWithoutTruncatingPersistedData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-maximum-todos-{Guid.NewGuid():N}");
        try
        {
            var store = new TodoStore(root);
            var createdAt = DateTimeOffset.UtcNow;
            var maximum = Enumerable.Range(0, TodoStore.MaximumTodoCount)
                .Select(index => new TodoItem($"id-{index}", $"todo-{index}", false, "normal", null, null, createdAt))
                .ToArray();
            await store.ReplaceAsync(maximum);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddAsync(new TodoMutation(null, "overflow", "normal", null, null)));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReplaceAsync(maximum.Append(
                    new TodoItem("overflow", "overflow", false, "normal", null, null, createdAt)).ToArray()));

            Assert.Equal(TodoStore.MaximumTodoCount, (await store.ListAsync()).Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TodoStore_ReplaceNormalizesDuplicateIdsFromBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-replace-todos-{Guid.NewGuid():N}");
        try
        {
            var store = new TodoStore(root);
            var now = DateTimeOffset.Now;

            var result = await store.ReplaceAsync(
            [
                new TodoItem("duplicate", "first", false, "normal", null, null, now),
                new TodoItem("duplicate", "second", false, "normal", null, null, now)
            ]);

            Assert.Equal(2, result.Count);
            Assert.Equal(2, result.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
