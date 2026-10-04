using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class ModelCatalogUpdateTests
{
    public static TheoryData<string, int, int, double, double, double, string> NewRates => new()
    {
        { "gpt-6.1-sol", 9, 29, 2, 0.1, 10, "2026.10.1" },
        { "gpt-rosalind-research", 10, 5, 5, 0.5, 25, "2026.10.1" },
        { "claude-fable-5-1", 9, 1, 10, 0.25, 50, "anthropic-2026.10.1" },
        { "claude-mythos-5-1", 9, 1, 10, 0.25, 50, "anthropic-2026.10.1" },
        { "claude-opus-5-5", 9, 22, 4, 0.2, 20, "anthropic-2026.10.1" },
        { "claude-sonnet-5-5", 9, 28, 2, 0.2, 10, "anthropic-2026.10.1" }
    };

    public static IEnumerable<object[]> CacheReadRates => NewRates
        .Select(row => new[] { row[0], row[1], row[2], row[3], row[4], row[6] });

    public static IEnumerable<object[]> ModelIdentities => NewRates
        .Select(row => new[] { row[0], row[1], row[2] });

    [Theory]
    [MemberData(nameof(NewRates))]
    public void NewModels_RateUsageOnlyFromTheirOfficialEffectiveDate(
        string model, int month, int day, double input, double cached, double output, string version)
    {
        var effective = new DateOnly(2026, month, day);
        var tokens = new TokenBreakdown(2_000_000, 1_000_000, 1_000_000, 0, 3_000_000);
        var before = UsageCredits.Calculate([new DatedModelTokenUsage(effective.AddDays(-1), model, tokens)]);

        Assert.Null(UsageCredits.FindRate(model, effective.AddDays(-1), null));
        Assert.Equal(0, before.CreditsUsed);
        Assert.Equal(3_000_000, before.UnratedTokens);
        Assert.Empty(before.ByModel);

        var rate = Assert.IsType<ModelCreditRate>(UsageCredits.FindRate(model, effective, null));
        Assert.Equal(model, rate.Model);
        Assert.Equal(effective, rate.EffectiveFrom);
        Assert.Equal(version, rate.CatalogVersion);
        Assert.Equal("exact", rate.MatchMode);
        Assert.False(string.IsNullOrWhiteSpace(rate.Source));
        Assert.Contains(model.StartsWith("claude-", StringComparison.Ordinal) ? "Anthropic" : "OpenAI API",
            rate.Source, StringComparison.Ordinal);
        Assert.Equal(input * 25, rate.InputCreditsPerMillion, 8);
        Assert.Equal(cached * 25, rate.CachedInputCreditsPerMillion, 8);
        Assert.Equal(output * 25, rate.OutputCreditsPerMillion, 8);
        Assert.Equal(rate, Assert.Single(UsageCredits.BuiltInRates, item => item.Model == model));

        var priced = UsageCredits.Calculate([new DatedModelTokenUsage(effective, model, tokens)]);
        Assert.Equal((input + cached + output) * 25, priced.CreditsUsed, 8);
        Assert.Equal(input + cached + output, UsageCredits.ToAmount(priced.CreditsUsed), 8);
        Assert.Equal(0, priced.UnratedTokens);
        var pricedModel = Assert.Single(priced.ByModel);
        Assert.Equal(model, pricedModel.Model);
        var provenance = Assert.Single(pricedModel.RateVersions);
        Assert.Equal(effective, provenance.EffectiveFrom);
        Assert.Equal(version, provenance.CatalogVersion);
        Assert.Equal(rate.Source, provenance.Source);
    }

    [Theory]
    [MemberData(nameof(CacheReadRates))]
    public void NewModels_UseTheirPublishedCacheReadPrice(
        string model, int month, int day, double input, double cached, string version)
    {
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(new DateOnly(2026, month, day), model,
                new TokenBreakdown(1_000_000, 1_000_000, 0, 0, 1_000_000))
        ]);

        var usage = Assert.Single(result.ByModel);
        Assert.Equal(0, usage.InputCredits);
        Assert.Equal(cached * 25, usage.CachedInputCredits, 8);
        Assert.Equal((input - cached) * 25, usage.CachedSavingsCredits, 8);
        Assert.Equal(cached, UsageCredits.ToAmount(result.CreditsUsed), 8);
        Assert.Equal(0, usage.OutputCredits);
        Assert.Equal(0, result.UnratedTokens);
        Assert.Equal(version, Assert.Single(usage.RateVersions).CatalogVersion);
    }

    [Theory]
    [MemberData(nameof(ModelIdentities))]
    public void NewModels_KeepCustomPricesAuthoritativeInExportedSnapshots(
        string model, int month, int day)
    {
        var custom = new ModelCreditRate(model, 1, 2, 3, Source: "my vendor", CatalogVersion: "mine-v1");
        var date = new DateOnly(2026, month, day).AddDays(1);
        Assert.Same(custom, UsageCredits.FindRate(model, date, [custom]));
        var exported = UsageCredits.CreateCatalogDocument([custom]);
        var exportedRow = Assert.Single(exported.Rates, item => item.Model == model);
        Assert.Equal(custom, exportedRow);

        var tokens = new TokenBreakdown(1_000_000, 250_000, 1_000_000, 0, 2_000_000);
        var priced = UsageCredits.Calculate([new DatedModelTokenUsage(date, model, tokens)],
            exported.Rates, completeRateCatalog: true);
        Assert.Equal(4.25, priced.CreditsUsed, 8);
        Assert.Equal(0, priced.UnratedTokens);
        Assert.Equal("mine-v1", Assert.Single(Assert.Single(priced.ByModel).RateVersions).CatalogVersion);
    }

    [Fact]
    public void PinnedOldCatalog_DoesNotAcquireNewRowsForEitherVendor()
    {
        ModelCreditRate[] pinned =
        [
            new("gpt-6-sol", 50, 5, 250, new DateOnly(2026, 9, 22), "archive", "2026.09.2"),
            new("claude-opus-5", 125, 12.5, 625, Source: "archive", CatalogVersion: "anthropic-2026.07.1")
        ];
        var date = new DateOnly(2026, 10, 5);
        var tokens = new TokenBreakdown(100, 0, 20, 0, 120);
        var models = NewRates.Select(row => (string)row[0]).ToArray();
        foreach (var model in models)
        {
            Assert.NotNull(UsageCredits.FindRate(model, date, pinned));
            Assert.Null(UsageCredits.FindRate(model, date, pinned, completeRateCatalog: true));
        }

        var result = UsageCredits.Calculate(models.Select(model => new DatedModelTokenUsage(date, model, tokens)),
            pinned, completeRateCatalog: true);
        Assert.Equal(720, result.UnratedTokens);
        Assert.Equal(0, result.CreditsUsed);
        Assert.Empty(result.ByModel);
        var snapshot = UsageCredits.CreateCatalogDocument(pinned, completeSnapshot: true,
            catalogVersion: "archive-v1", source: "archive", baseCatalogVersion: "2026.09.2");
        Assert.Equal(pinned.OrderBy(rate => rate.Model, StringComparer.Ordinal), snapshot.Rates);
        Assert.Equal("2026.09.2", snapshot.BaseCatalogVersion);
    }

    [Fact]
    public void OldAndNewSol_RemainDistinctWithDifferentCacheReadPrices()
    {
        var date = new DateOnly(2026, 10, 5);
        var cachedOnly = new TokenBreakdown(1_000_000, 1_000_000, 0, 0, 1_000_000);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(date, "gpt-6-sol", cachedOnly),
            new DatedModelTokenUsage(date, "gpt-6.1-sol", cachedOnly)
        ]);

        Assert.Equal(2, result.ByModel.Count);
        Assert.Equal(7.5, result.CreditsUsed, 8);
        Assert.Equal(0, result.UnratedTokens);
        Assert.Equal(5, Assert.Single(result.ByModel, item => item.Model == "gpt-6-sol").CachedInputCredits);
        Assert.Equal(2.5, Assert.Single(result.ByModel, item => item.Model == "gpt-6.1-sol").CachedInputCredits);
        var oldRate = Assert.IsType<ModelCreditRate>(UsageCredits.FindRate("gpt-6-sol", date, null));
        Assert.Equal("2026.09.2", oldRate.CatalogVersion);
        Assert.Equal(new DateOnly(2026, 9, 22), oldRate.EffectiveFrom);
    }

    [Theory]
    [InlineData("gpt-6.1")]
    [InlineData("gpt-6.1-codex")]
    [InlineData("gpt-6.1-sol-unannounced")]
    [InlineData("gpt-rosalind")]
    [InlineData("gpt-rosalind-research-premium")]
    [InlineData("claude-fable-5-1-unannounced")]
    [InlineData("claude-mythos-5-1-unannounced")]
    [InlineData("claude-opus-5-5-unannounced")]
    [InlineData("claude-sonnet-5-5-unannounced")]
    public void NewModels_DoNotGuessFamilyOrVariantPrices(string model)
    {
        var date = new DateOnly(2026, 10, 5);
        Assert.Null(UsageCredits.FindRate(model, date, null));
        var result = UsageCredits.Calculate(
            [new DatedModelTokenUsage(date, model, new TokenBreakdown(100, 0, 20, 0, 120))]);
        Assert.Equal(120, result.UnratedTokens);
        Assert.Equal(0, result.CreditsUsed);
        Assert.Empty(result.ByModel);
    }
}
