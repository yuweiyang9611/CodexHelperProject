using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class UsageMathTests
{
    [Fact]
    public void RemainingPercent_RejectsNonFiniteUsage()
    {
        Assert.Equal(0, new RateLimitWindow(double.NaN, null, null).RemainingPercent);
        Assert.Equal(0, new RateLimitWindow(double.PositiveInfinity, null, null).RemainingPercent);
    }

    [Fact]
    public void RateLimitClassifier_UsesDurationInsteadOfProtocolFieldName()
    {
        var weeklyInPrimaryField = new RateLimitWindow(31, 10_080, DateTimeOffset.UtcNow.AddDays(6));

        var (fiveHour, sevenDay) = RateLimitWindowClassifier.Classify(weeklyInPrimaryField, null);

        Assert.Null(fiveHour);
        Assert.Same(weeklyInPrimaryField, sevenDay);
    }

    [Fact]
    public void RateLimitClassifier_HandlesSwappedNamedWindows()
    {
        var weekly = new RateLimitWindow(31, 10_080, null);
        var fiveHours = new RateLimitWindow(12, 300, null);

        var (fiveHour, sevenDay) = RateLimitWindowClassifier.Classify(weekly, fiveHours);

        Assert.Same(fiveHours, fiveHour);
        Assert.Same(weekly, sevenDay);
    }

    [Fact]
    public void PositiveDelta_ClampsCounterResets()
    {
        var previous = new TokenBreakdown(100, 30, 40, 8, 140);
        var current = new TokenBreakdown(80, 35, 60, 6, 145);

        var delta = current.PositiveDelta(previous);

        Assert.Equal(0, delta.InputTokens);
        Assert.Equal(5, delta.CachedInputTokens);
        Assert.Equal(20, delta.OutputTokens);
        Assert.Equal(0, delta.ReasoningOutputTokens);
        Assert.Equal(5, delta.TotalTokens);
    }

    [Fact]
    public void PositiveDelta_CountsFirstSampleAfterTotalCounterRestart()
    {
        var previous = new TokenBreakdown(1_000, 400, 300, 80, 1_300);
        var current = new TokenBreakdown(120, 50, 30, 8, 150);

        var delta = current.PositiveDelta(previous);

        Assert.Equal(current, delta);
    }

    [Fact]
    public void PositiveDelta_DoesNotTreatAMissingTotalFieldAsACounterRestart()
    {
        var previous = new TokenBreakdown(100, 30, 40, 8, 140);
        var current = new TokenBreakdown(120, 35, 60, 10, 0);

        var delta = current.PositiveDelta(previous);

        Assert.Equal(20, delta.InputTokens);
        Assert.Equal(5, delta.CachedInputTokens);
        Assert.Equal(20, delta.OutputTokens);
        Assert.Equal(40, delta.VisibleTotalTokens);
    }

    [Fact]
    public void VisibleTotal_UsesSplitWhenTotalIsMissing()
    {
        var usage = new TokenBreakdown(120, 50, 30, 5, 0);

        Assert.Equal(150, usage.VisibleTotalTokens);
        Assert.Equal(70, usage.UncachedInputTokens);
        Assert.Equal(50, usage.BillableCachedInputTokens);
    }

    [Theory]
    [InlineData(1_700_000_000L, 2023)]
    [InlineData(1_700_000_000_000L, 2023)]
    public void FromUnixTime_SupportsSecondsAndMilliseconds(long value, int expectedYear)
    {
        Assert.Equal(expectedYear, UsageCredits.FromUnixTime(value)?.Year);
    }

    [Fact]
    public void Calculate_UsesModelSpecificUncachedCachedAndOutputRates()
    {
        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage(
                "gpt-5.6-sol",
                new TokenBreakdown(1_000_000, 200_000, 100_000, 20_000, 1_100_000))
        ]);

        Assert.Equal(177.5d, result.CreditsUsed, precision: 6);
        Assert.Equal(0, result.UnratedTokens);
        var model = Assert.Single(result.ByModel);
        Assert.Equal(100d, model.InputCredits, precision: 6);
        Assert.Equal(2.5d, model.CachedInputCredits, precision: 6);
        Assert.Equal(75d, model.OutputCredits, precision: 6);
        Assert.Equal(22.5d, model.CachedSavingsCredits, precision: 6);
    }

    [Fact]
    public void Calculate_LeavesResearchPreviewAndUnknownModelsUnrated()
    {
        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage("gpt-5.3-codex-spark", new TokenBreakdown(100, 0, 20, 0, 120)),
            new ModelTokenUsage("future-model", new TokenBreakdown(200, 0, 40, 0, 240))
        ]);

        Assert.Equal(0, result.CreditsUsed);
        Assert.Equal(360, result.UnratedTokens);
    }

    [Theory]
    [InlineData("gpt-5.6-sol", 125d, 12.5d, 750d)]
    [InlineData("gpt-5.6-terra", 62.5d, 6.25d, 375d)]
    [InlineData("gpt-5.6-luna", 25d, 2.5d, 150d)]
    [InlineData("gpt-5.5", 125d, 12.5d, 750d)]
    [InlineData("gpt-5.5-cyber", 500d, 50d, 3_000d)]
    [InlineData("gpt-5.4", 62.5d, 6.25d, 375d)]
    [InlineData("gpt-5.4-mini", 18.75d, 1.875d, 113d)]
    [InlineData("gpt-5.3-codex", 43.75d, 4.375d, 350d)]
    [InlineData("gpt-5.2", 43.75d, 4.375d, 350d)]
    [InlineData("gpt-image-2.0-image", 200d, 50d, 750d)]
    [InlineData("gpt-image-2.0-text", 125d, 31.25d, 250d)]
    public void FindRate_MatchesReferenceTable(
        string model,
        double input,
        double cachedInput,
        double output)
    {
        var rate = Assert.IsType<ModelCreditRate>(UsageCredits.FindRate(model));

        Assert.Equal(input, rate.InputCreditsPerMillion);
        Assert.Equal(cachedInput, rate.CachedInputCreditsPerMillion);
        Assert.Equal(output, rate.OutputCreditsPerMillion);
    }

    [Fact]
    public void Calculate_MergesAliasesIntoTheSameRatedModel()
    {
        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage("gpt-5.2", new TokenBreakdown(100, 0, 0, 0, 100)),
            new ModelTokenUsage("gpt-5.2-codex", new TokenBreakdown(200, 0, 0, 0, 200))
        ]);

        var model = Assert.Single(result.ByModel);
        Assert.Equal("gpt-5.2", model.Model);
        Assert.Equal(300, model.Tokens.InputTokens);
    }

    [Fact]
    public void Calculate_SeparateModelsMatchedByOnePrefixRemainSeparate()
    {
        var rates = new[]
        {
            new ModelCreditRate("claude", 10, 1, 100, MatchMode: "prefix")
        };

        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage("claude-opus-4", new TokenBreakdown(100, 0, 0, 0, 100)),
            new ModelTokenUsage("claude-sonnet-4", new TokenBreakdown(200, 0, 0, 0, 200))
        ], rates);

        Assert.Equal(2, result.ByModel.Count);
        Assert.Contains(result.ByModel, item => item.Model == "claude-opus-4" && item.Tokens.InputTokens == 100);
        Assert.Contains(result.ByModel, item => item.Model == "claude-sonnet-4" && item.Tokens.InputTokens == 200);
    }

    [Fact]
    public void Calculate_SameModelRemainsGroupedWhenItsHistoricalPrefixMatchChanges()
    {
        var rates = new[]
        {
            new ModelCreditRate(
                "claude",
                10,
                1,
                100,
                new DateOnly(2026, 1, 1),
                "vendor",
                "v1",
                "prefix"),
            new ModelCreditRate(
                "claude-sonnet",
                20,
                2,
                200,
                new DateOnly(2026, 7, 1),
                "vendor",
                "v2",
                "prefix")
        };

        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(
                new DateOnly(2026, 6, 30),
                "claude-sonnet-4",
                new TokenBreakdown(100, 0, 0, 0, 100)),
            new DatedModelTokenUsage(
                new DateOnly(2026, 7, 2),
                "claude-sonnet-4",
                new TokenBreakdown(200, 0, 0, 0, 200))
        ], rates);

        var model = Assert.Single(result.ByModel);
        Assert.Equal("claude-sonnet-4", model.Model);
        Assert.Equal(300, model.Tokens.InputTokens);
        Assert.Equal(2, model.RateVersions.Count);
    }

    [Fact]
    public void Calculate_DoesNotGuessBuiltInRateForUnknownModelSuffix()
    {
        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage(
                "gpt-5.2-premium",
                new TokenBreakdown(100, 0, 50, 0, 150))
        ]);

        Assert.Equal(0, result.CreditsUsed);
        Assert.Equal(150, result.UnratedTokens);
        Assert.Empty(result.ByModel);
    }

    [Fact]
    public void Calculate_CustomRateOverridesBuiltInRate()
    {
        var customRates = new[] { new ModelCreditRate("gpt-5.2", 1, 2, 3) };

        var result = UsageCredits.Calculate(
        [
            new ModelTokenUsage("gpt-5.2-codex", new TokenBreakdown(1_000_000, 250_000, 1_000_000, 0, 2_000_000))
        ], customRates);

        var model = Assert.Single(result.ByModel);
        Assert.Equal(4.25d, model.TotalCredits, precision: 6);
        Assert.Equal("gpt-5.2", model.Model);
    }

    [Fact]
    public void FindRate_PrefersMostSpecificCustomModelPrefix()
    {
        var customRates = new[]
        {
            new ModelCreditRate("claude", 1, 1, 1, MatchMode: "prefix"),
            new ModelCreditRate("claude-sonnet", 2, 2, 2, MatchMode: "prefix")
        };

        var rate = UsageCredits.FindRate("claude-sonnet-4-5", customRates);

        Assert.NotNull(rate);
        Assert.Equal("claude-sonnet", rate.Model);
    }

    [Fact]
    public void FindRate_NormalizesLatestAfterTheCodexAlias()
    {
        var rate = UsageCredits.FindRate("gpt-5.2-codex-latest");

        Assert.NotNull(rate);
        Assert.Equal("gpt-5.2", rate.Model);
    }

    [Fact]
    public void FindRate_CompleteCatalogDoesNotFallBackToTheCurrentBuiltInCatalog()
    {
        var historicalSnapshot = new[]
        {
            new ModelCreditRate("gpt-5.2", 10, 1, 100, Source: "archive", CatalogVersion: "v1")
        };

        Assert.NotNull(UsageCredits.FindRate("gpt-5.6-sol", historicalSnapshot));
        Assert.Null(UsageCredits.FindRate(
            "gpt-5.6-sol",
            historicalSnapshot,
            completeRateCatalog: true));

        var calculation = UsageCredits.Calculate(
        [
            new ModelTokenUsage("gpt-5.6-sol", new TokenBreakdown(100, 0, 20, 0, 120))
        ], historicalSnapshot, completeRateCatalog: true);
        Assert.Equal(120, calculation.UnratedTokens);
        Assert.Empty(calculation.ByModel);
    }

    [Fact]
    public void CreateCatalogDocument_CompleteSnapshotPreservesRowsAndTopLevelMetadata()
    {
        var snapshotRates = new[]
        {
            new ModelCreditRate("archive-model", 10, 1, 100)
        };

        var document = UsageCredits.CreateCatalogDocument(
            snapshotRates,
            completeSnapshot: true,
            catalogVersion: "archive-v1",
            source: "archived vendor table",
            baseCatalogVersion: "vendor-base-v1");

        var rate = Assert.Single(document.Rates);
        Assert.Equal("archive-model", rate.Model);
        Assert.Equal("archive-v1", rate.CatalogVersion);
        Assert.Equal("archived vendor table", rate.Source);
        Assert.Equal("archive-v1", document.CatalogVersion);
        Assert.Equal("archived vendor table", document.Source);
        Assert.Equal("vendor-base-v1", document.BaseCatalogVersion);
        Assert.DoesNotContain(document.Rates, UsageCredits.IsBuiltInRate);
    }

    [Fact]
    public void BuiltInCatalog_PreservesPublished2026071RowMetadata()
    {
        var publishedModels = new HashSet<string>(StringComparer.Ordinal)
        {
            "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5-cyber", "gpt-5.5",
            "gpt-5.4-mini", "gpt-5.4", "gpt-5.3-codex", "gpt-5.2",
            "gpt-image-2.0-image", "gpt-image-2.0-text"
        };
        var publishedRows = UsageCredits.BuiltInRates
            .Where(rate => publishedModels.Contains(rate.Model) && rate.EffectiveFrom is null)
            .ToArray();

        Assert.Equal(publishedModels.Count, publishedRows.Length);
        Assert.All(publishedRows, rate =>
        {
            Assert.Equal("2026.07.1", rate.CatalogVersion);
            Assert.Equal("用户提供的 OpenAI Credits 参考表", rate.Source);
        });
    }

    [Theory]
    // A dated snapshot id prices exactly like its alias, and built-in rows are matched
    // exactly — so without collapsing the suffix these would all go unrated, which is
    // the very failure the Anthropic rows were added to fix.
    [InlineData("claude-opus-5-20260514", "claude-opus-5")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("claude-sonnet-5-latest", "claude-sonnet-5")]
    [InlineData("gpt-5.2-20260101", "gpt-5.2")]
    public void NormalizeModel_CollapsesDatedSnapshotIdsOntoTheirAlias(string model, string expected) =>
        Assert.Equal(expected, UsageCredits.NormalizeModel(model));

    [Theory]
    // Only a plausible calendar date is a snapshot suffix.
    [InlineData("model-99999999")]
    [InlineData("model-20261301")]
    [InlineData("model-20260132")]
    [InlineData("model-2026051")]
    public void NormalizeModel_KeepsTrailingDigitsThatAreNotADate(string model) =>
        Assert.Equal(model, UsageCredits.NormalizeModel(model));

    [Fact]
    public void FindRate_ResolvesDatedClaudeIdsThroughTheAliasRow()
    {
        var dated = UsageCredits.FindRate("claude-opus-5-20260514", new DateOnly(2026, 7, 29), null);
        var alias = UsageCredits.FindRate("claude-opus-5", new DateOnly(2026, 7, 29), null);

        Assert.NotNull(dated);
        Assert.Equal(alias!.InputCreditsPerMillion, dated.InputCreditsPerMillion);
        Assert.Equal(alias.OutputCreditsPerMillion, dated.OutputCreditsPerMillion);
    }

    [Fact]
    public void CreateCatalogDocument_DoesNotLetABuiltInDateSupersedeAUserPrice()
    {
        // claude-sonnet-5 is the first model whose built-in lineage spans two effective
        // dates. A user's own undated price must keep applying past 2026-09-01 rather
        // than being silently replaced by the built-in standard rate on that date.
        var mine = new ModelCreditRate("claude-sonnet-5", 10, 1, 20, null, "my vendor", "mine-v1");

        var document = UsageCredits.CreateCatalogDocument([mine]);
        var rows = document.Rates
            .Where(rate => UsageCredits.NormalizeModel(rate.Model) == "claude-sonnet-5")
            .ToArray();

        var row = Assert.Single(rows);
        Assert.Equal("mine-v1", row.CatalogVersion);
        Assert.Null(row.EffectiveFrom);

        var afterTheBuiltInDate = UsageCredits.FindRate(
            "claude-sonnet-5",
            new DateOnly(2026, 12, 1),
            document.Rates);
        Assert.NotNull(afterTheBuiltInDate);
        Assert.Equal(10, afterTheBuiltInDate.InputCreditsPerMillion);
    }

    [Fact]
    public void CreateCatalogDocument_KeepsBuiltInLineagesForModelsTheUserDidNotPrice()
    {
        var mine = new ModelCreditRate("claude-sonnet-5", 10, 1, 20, null, "my vendor", "mine-v1");

        var document = UsageCredits.CreateCatalogDocument([mine]);

        // Untouched models keep every built-in row, including dated ones.
        Assert.Contains(document.Rates, rate => UsageCredits.NormalizeModel(rate.Model) == "claude-opus-5");
        Assert.Contains(document.Rates, rate => UsageCredits.NormalizeModel(rate.Model) == "gpt-5.2");
    }

    [Fact]
    public void InferMonthlyAmount_ReadsProAsADifferentPricePerRuntime()
    {
        // The collision that made this runtime-aware: Claude Pro is US$20 a month,
        // ChatGPT Pro is US$200. Resolving one table for both overstated a Claude
        // Pro subscription tenfold and silently wrecked the payback multiple.
        Assert.Equal(20d, SubscriptionPricing.InferMonthlyAmount("pro", AgentRuntime.ClaudeCode));
        Assert.Equal(200d, SubscriptionPricing.InferMonthlyAmount("pro", AgentRuntime.Codex));
    }

    [Theory]
    [InlineData("free", 0d)]
    [InlineData("pro", 20d)]
    [InlineData("max", 100d)]
    [InlineData("Max 5x", 100d)]
    [InlineData("max_20x", 200d)]
    public void InferMonthlyAmount_MapsClaudePlans(string planType, double expected) =>
        Assert.Equal(expected, SubscriptionPricing.InferMonthlyAmount(planType, AgentRuntime.ClaudeCode));

    [Theory]
    [InlineData("team")]
    [InlineData("enterprise")]
    [InlineData("plus")]
    public void InferMonthlyAmount_LeavesUnpricedClaudePlansToTheManualSetting(string planType)
    {
        // Team and Enterprise are per-seat and negotiated, and "plus" is a ChatGPT
        // plan that must not leak across. Null defers to the manual amount rather
        // than inventing a bill.
        Assert.Null(SubscriptionPricing.InferMonthlyAmount(planType, AgentRuntime.ClaudeCode));
    }

    [Fact]
    public void AccountSnapshot_CarriesItsRuntimeIntoTheSuggestedAmount()
    {
        var claude = new AccountSnapshot("claude-code", "pro", null, true, AgentRuntime.ClaudeCode);
        var codex = new AccountSnapshot("chatgpt", "pro", null, true);

        Assert.Equal(20d, claude.SuggestedMonthlySubscriptionAmount);
        Assert.Equal(200d, codex.SuggestedMonthlySubscriptionAmount);
    }

    [Fact]
    public void TokenBreakdown_SplitsInputIntoPlainCachedAndCacheWriteSlices()
    {
        var tokens = new TokenBreakdown(1_000, 300, 0, 0, 1_000, CacheWrite5mTokens: 200, CacheWrite1hTokens: 100);

        Assert.Equal(300, tokens.BillableCachedInputTokens);
        Assert.Equal(200, tokens.BillableCacheWrite5mTokens);
        Assert.Equal(100, tokens.BillableCacheWrite1hTokens);
        // The three slices partition InputTokens exactly — no double counting.
        Assert.Equal(400, tokens.UncachedInputTokens);
        Assert.Equal(
            tokens.InputTokens,
            tokens.UncachedInputTokens + tokens.BillableCachedInputTokens + tokens.BillableCacheWriteTokens);
    }

    [Fact]
    public void TokenBreakdown_WithoutCacheWriteSplit_PricesWholeRemainderAsPlainInput()
    {
        // Codex reports no cache-write split, so its rows must be unaffected.
        var tokens = new TokenBreakdown(1_000, 300, 0, 0, 1_000);

        Assert.Equal(0, tokens.BillableCacheWriteTokens);
        Assert.Equal(700, tokens.UncachedInputTokens);
    }

    [Fact]
    public void TokenBreakdown_ClampsCacheWritesToTheInputItActuallyHas()
    {
        // Malformed input must never make the slices exceed InputTokens.
        var tokens = new TokenBreakdown(500, 300, 0, 0, 500, CacheWrite5mTokens: 400, CacheWrite1hTokens: 400);

        Assert.Equal(200, tokens.BillableCacheWriteTokens);
        Assert.Equal(0, tokens.UncachedInputTokens);
        Assert.Equal(
            tokens.InputTokens,
            tokens.UncachedInputTokens + tokens.BillableCachedInputTokens + tokens.BillableCacheWriteTokens);
    }

    [Fact]
    public void ComputeCredits_PricesCacheWritesAboveBaseInput()
    {
        // 1M plain input, 1M written at the 5 minute TTL, 1M at the 1 hour TTL.
        var rate = new ModelCreditRate("write-model", 100, 10, 200);
        var tokens = new TokenBreakdown(
            3_000_000,
            0,
            0,
            0,
            3_000_000,
            CacheWrite5mTokens: 1_000_000,
            CacheWrite1hTokens: 1_000_000);

        var usage = Assert.Single(UsageCredits.Calculate(
            [new ModelTokenUsage("write-model", tokens)],
            [rate]).ByModel);

        Assert.Equal(100, usage.InputCredits, precision: 6);
        // 1M at 1.25x plus 1M at 2x of the 100/M base input rate.
        Assert.Equal(325, usage.CacheWriteCredits, precision: 6);
        Assert.Equal(425, usage.TotalCredits, precision: 6);
    }

    [Theory]
    // Anthropic list prices at CreditsPerDollar, cached input at the 0.1x cache-read multiple.
    [InlineData("claude-fable-5", 250d, 25d, 1_250d)]
    [InlineData("claude-mythos-5", 250d, 25d, 1_250d)]
    [InlineData("claude-opus-5", 125d, 12.5d, 625d)]
    [InlineData("claude-opus-4-8", 125d, 12.5d, 625d)]
    [InlineData("claude-sonnet-4-6", 75d, 7.5d, 375d)]
    [InlineData("claude-haiku-4-5", 25d, 2.5d, 125d)]
    [InlineData("claude-haiku-4-5-20251001", 25d, 2.5d, 125d)]
    public void BuiltInCatalog_RatesClaudeModels(string model, double input, double cachedInput, double output)
    {
        // Before these rows existed every Claude token fell into UnratedTokens, so the
        // equivalent-value card read US$0.00 for anyone using Claude Code.
        var rate = UsageCredits.FindRate(model, new DateOnly(2026, 7, 29), null);

        Assert.NotNull(rate);
        Assert.Equal(input, rate.InputCreditsPerMillion);
        Assert.Equal(cachedInput, rate.CachedInputCreditsPerMillion);
        Assert.Equal(output, rate.OutputCreditsPerMillion);
    }

    [Fact]
    public void BuiltInCatalog_ReplaysSonnet5IntroductoryPricingByUsageDate()
    {
        // Introductory pricing lapses after 2026-08-31; usage before that keeps billing
        // at the introductory rate rather than being repriced by the later row.
        var introductory = UsageCredits.FindRate("claude-sonnet-5", new DateOnly(2026, 8, 31), null);
        var standard = UsageCredits.FindRate("claude-sonnet-5", new DateOnly(2026, 9, 1), null);

        Assert.NotNull(introductory);
        Assert.Equal(50d, introductory.InputCreditsPerMillion);
        Assert.Equal(250d, introductory.OutputCreditsPerMillion);

        Assert.NotNull(standard);
        Assert.Equal(75d, standard.InputCreditsPerMillion);
        Assert.Equal(375d, standard.OutputCreditsPerMillion);
    }

    [Fact]
    public void FindRate_SelectsLatestVersionEffectiveOnUsageDate()
    {
        var rates = new[]
        {
            new ModelCreditRate(
                "history-model",
                20,
                2,
                200,
                new DateOnly(2026, 7, 1),
                "vendor pricing",
                "v2"),
            new ModelCreditRate(
                "history-model",
                10,
                1,
                100,
                new DateOnly(2026, 1, 1),
                "vendor pricing",
                "v1")
        };

        var beforeChange = UsageCredits.FindRate(
            "history-model-latest",
            new DateOnly(2026, 6, 30),
            rates);
        var onChange = UsageCredits.FindRate(
            "history-model-latest",
            new DateOnly(2026, 7, 1),
            rates);

        Assert.NotNull(beforeChange);
        Assert.Equal("v1", beforeChange.CatalogVersion);
        Assert.Equal(10, beforeChange.InputCreditsPerMillion);
        Assert.NotNull(onChange);
        Assert.Equal("v2", onChange.CatalogVersion);
        Assert.Equal(20, onChange.InputCreditsPerMillion);
    }

    [Fact]
    public void FindRate_CustomRateOverridesBuiltInWithoutApplyingFutureVersionToHistory()
    {
        var customRates = new[]
        {
            new ModelCreditRate(
                "gpt-5.2",
                10,
                1,
                100,
                new DateOnly(2026, 1, 1),
                "customer contract",
                "custom-v1"),
            new ModelCreditRate(
                "gpt-5.2",
                900,
                90,
                9_000,
                new DateOnly(2027, 1, 1),
                "customer contract",
                "custom-v2")
        };

        var beforeAnyCustomRate = UsageCredits.FindRate(
            "gpt-5.2-codex",
            new DateOnly(2025, 12, 31),
            customRates);
        var duringFirstCustomVersion = UsageCredits.FindRate(
            "gpt-5.2-codex",
            new DateOnly(2026, 7, 1),
            customRates);

        Assert.NotNull(beforeAnyCustomRate);
        Assert.Equal(UsageCredits.CurrentCatalogVersion, beforeAnyCustomRate.CatalogVersion);
        Assert.NotNull(duringFirstCustomVersion);
        Assert.Equal("custom-v1", duringFirstCustomVersion.CatalogVersion);
        Assert.Equal(10, duringFirstCustomVersion.InputCreditsPerMillion);
    }

    [Fact]
    public void Calculate_AggregatesSameModelAcrossHistoricalRateVersions()
    {
        var rates = new[]
        {
            new ModelCreditRate(
                "history-model",
                10,
                2,
                30,
                new DateOnly(2026, 1, 1),
                "vendor pricing",
                "v1"),
            new ModelCreditRate(
                "history-model",
                20,
                4,
                60,
                new DateOnly(2026, 7, 1),
                "vendor pricing",
                "v2")
        };

        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(
                new DateOnly(2026, 6, 30),
                "history-model",
                new TokenBreakdown(1_000_000, 250_000, 500_000, 0, 1_500_000)),
            new DatedModelTokenUsage(
                new DateOnly(2026, 7, 2),
                "history-model-latest",
                new TokenBreakdown(2_000_000, 500_000, 1_000_000, 0, 3_000_000))
        ], rates);

        Assert.Equal(115d, result.CreditsUsed, precision: 6);
        Assert.Equal(0, result.UnratedTokens);
        var model = Assert.Single(result.ByModel);
        Assert.Equal("history-model", model.Model);
        Assert.Equal(3_000_000, model.Tokens.InputTokens);
        Assert.Equal(750_000, model.Tokens.CachedInputTokens);
        Assert.Equal(1_500_000, model.Tokens.OutputTokens);
        Assert.Equal(37.5d, model.InputCredits, precision: 6);
        Assert.Equal(2.5d, model.CachedInputCredits, precision: 6);
        Assert.Equal(75d, model.OutputCredits, precision: 6);
        Assert.Equal(10d, model.CachedSavingsCredits, precision: 6);
        Assert.Collection(
            model.RateVersions,
            first =>
            {
                Assert.Equal("v1", first.CatalogVersion);
                Assert.Equal(new DateOnly(2026, 1, 1), first.EffectiveFrom);
                Assert.Equal(23d, first.TotalCredits, precision: 6);
                Assert.Equal(1_500_000, first.Tokens.VisibleTotalTokens);
            },
            second =>
            {
                Assert.Equal("v2", second.CatalogVersion);
                Assert.Equal(new DateOnly(2026, 7, 1), second.EffectiveFrom);
                Assert.Equal(92d, second.TotalCredits, precision: 6);
                Assert.Equal(3_000_000, second.Tokens.VisibleTotalTokens);
            });
    }

    [Fact]
    public void SettingsNormalize_SanitizesNewAlertHotkeyAndCustomRateFields()
    {
        var settings = new AppSettings(
            MonthlyAmountAlert: double.NaN,
            MinimumRateCoverageAlertPercent: 500,
            GlobalHotKey: "Alt+Delete",
            StatusStripQuotaMode: "USED",
            CustomModelRates:
            [
                new ModelCreditRate(" Claude_Sonnet ", double.NaN, -1, 2),
                new ModelCreditRate("claude-sonnet", 3, 4, 5)
            ]).Normalize();

        Assert.Equal(0, settings.MonthlyAmountAlert);
        Assert.Equal(100, settings.MinimumRateCoverageAlertPercent);
        Assert.Equal(HotKeyGesture.Default, settings.GlobalHotKey);
        Assert.Equal("used", settings.StatusStripQuotaMode);
        var rate = Assert.Single(settings.CustomModelRates!);
        Assert.Equal(new ModelCreditRate("claude-sonnet", 3, 4, 5), rate);
    }

    [Fact]
    public void PurchaseReference_IsTwentyFiveCreditsPerDollar()
    {
        Assert.Equal(UsageCredits.CreditsPerTwentyDollars, UsageCredits.CreditsPerDollar * 20);
        Assert.Equal(UsageCredits.CreditsPerFortyDollars, UsageCredits.CreditsPerDollar * 40);
        Assert.Equal(UsageCredits.CreditsPerEightyDollars, UsageCredits.CreditsPerDollar * 80);
    }

    [Theory]
    [InlineData(500d, 40d, 20d)]
    [InlineData(1_000d, 40d, 40d)]
    [InlineData(2_000d, 40d, 80d)]
    [InlineData(1_000d, 280d, 280d)]
    public void ToAmount_UsesConfigurableAmountPerThousandCredits(
        double credits,
        double amountPerThousandCredits,
        double expected)
    {
        Assert.Equal(expected, UsageCredits.ToAmount(credits, amountPerThousandCredits), precision: 6);
    }

    [Fact]
    public void ToAmount_DoesNotLeakNonFiniteValuesIntoTheUi()
    {
        Assert.Equal(0, UsageCredits.ToAmount(double.NaN));
        Assert.Equal(40, UsageCredits.ToAmount(1_000, double.PositiveInfinity));
    }

    [Theory]
    [InlineData("free", 0d)]
    [InlineData("plus", 20d)]
    [InlineData("prolite", 100d)]
    [InlineData("pro-lite", 100d)]
    [InlineData("pro", 200d)]
    public void InferMonthlyAmount_MapsKnownLocalPlanLabels(string planType, double expected)
    {
        Assert.Equal(expected, SubscriptionPricing.InferMonthlyAmount(planType));
    }

    [Theory]
    [InlineData("business")]
    [InlineData("enterprise")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void InferMonthlyAmount_DoesNotGuessVariableOrUnknownPlans(string? planType)
    {
        Assert.Null(SubscriptionPricing.InferMonthlyAmount(planType));
    }

    [Fact]
    public void FindRate_PinnedOpenAiCatalogStillPricesClaudeFromBuiltIns()
    {
        // Importing an archive to reproduce a historical ChatGPT bill pins
        // unconditionally. Suppressing built-ins outright then left every claude-*
        // model unrated: US$0.00 beside a real token count, 0% coverage, and a tray
        // alert asking for rates the user never meant to manage. The snapshot said
        // nothing about Anthropic, so it was never claiming to describe it.
        var openAiArchive = new[] { new ModelCreditRate("gpt-5.2", 43.75, 4.375, 350) };

        var claude = UsageCredits.FindRate("claude-opus-5", openAiArchive, completeRateCatalog: true);

        Assert.NotNull(claude);
        Assert.Equal(125, claude.InputCreditsPerMillion);
    }

    [Fact]
    public void FindRate_PinnedOpenAiCatalogStillGovernsOpenAiModels()
    {
        // The vendor the snapshot does cover must stay pinned — that is the whole
        // point of pinning.
        var openAiArchive = new[] { new ModelCreditRate("gpt-5.2", 43.75, 4.375, 350) };

        Assert.Null(UsageCredits.FindRate("gpt-5.4", openAiArchive, completeRateCatalog: true));
        Assert.Equal(43.75, UsageCredits.FindRate("gpt-5.2", openAiArchive, completeRateCatalog: true)?.InputCreditsPerMillion);
    }

    [Fact]
    public void FindRate_PinnedCatalogWithUnclassifiableNamesSuppressesEverything()
    {
        // No positive evidence the snapshot is vendor-specific, so the safe reading is
        // the old one. Silently re-pricing a pinned snapshot would change numbers the
        // user pinned precisely so they would not change.
        var privateArchive = new[] { new ModelCreditRate("archive-model", 10, 1, 100) };

        Assert.Null(UsageCredits.FindRate("gpt-5.2", privateArchive, completeRateCatalog: true));
        Assert.Null(UsageCredits.FindRate("claude-opus-5", privateArchive, completeRateCatalog: true));
    }

    [Fact]
    public void FindRate_ResearchPreviewCarveOutAppliesToItsOwnLineageOnly()
    {
        // An unanchored substring test applied to both vendors: a future Anthropic id
        // containing the word would have dropped silently into unrated tokens.
        Assert.Null(UsageCredits.FindRate("gpt-5.3-codex-spark"));
        Assert.NotNull(UsageCredits.FindRate("claude-opus-5"));
    }

    [Theory]
    [InlineData("claude-opus-5", ModelLineage.Anthropic)]
    [InlineData("claude-haiku-4-5-20251001", ModelLineage.Anthropic)]
    [InlineData("gpt-5.2", ModelLineage.OpenAi)]
    [InlineData("gpt-image-2.0-text", ModelLineage.OpenAi)]
    [InlineData("archive-model", ModelLineage.Unknown)]
    [InlineData("", ModelLineage.Unknown)]
    public void LineageOf_ClassifiesByVendorNamespace(string model, ModelLineage expected) =>
        Assert.Equal(expected, UsageCredits.LineageOf(model));
}
