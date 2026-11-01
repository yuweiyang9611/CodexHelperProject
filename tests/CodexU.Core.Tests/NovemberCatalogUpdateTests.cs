using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class NovemberCatalogUpdateTests
{
    public static TheoryData<string, int, double, double, double, string> NewModels => new()
    {
        { "gpt-6.1-terra", 26, 1, 0.05, 5, "2026.11.1" },
        { "gpt-6-rosalind", 30, 15, 1.5, 75, "2026.11.1" },
        { "claude-haiku-5-5", 7, 0.1, 0.01, 0.5, "anthropic-2026.11.1" }
    };

    [Theory]
    [MemberData(nameof(NewModels))]
    public void NewModels_PriceOnlyFromReleaseAndPreserveProvenance(
        string model, int day, double input, double cached, double output, string version)
    {
        var release = new DateOnly(2026, 10, day);
        var tokens = new TokenBreakdown(2_000_000, 1_000_000, 1_000_000, 0, 3_000_000);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(release.AddDays(-1), model, tokens),
            new DatedModelTokenUsage(release, model, tokens)
        ]);

        Assert.Null(UsageCredits.FindRate(model, release.AddDays(-1), null));
        Assert.Equal(3_000_000, result.UnratedTokens);
        Assert.Equal(input + cached + output, UsageCredits.ToAmount(result.CreditsUsed), 8);
        var priced = Assert.Single(result.ByModel);
        Assert.Equal(input * 25, priced.InputCredits, 8);
        Assert.Equal(cached * 25, priced.CachedInputCredits, 8);
        Assert.Equal(output * 25, priced.OutputCredits, 8);
        var provenance = Assert.Single(priced.RateVersions);
        Assert.Equal(release, provenance.EffectiveFrom);
        Assert.Equal(version, provenance.CatalogVersion);

        var rate = Assert.IsType<ModelCreditRate>(UsageCredits.FindRate(model, release, null));
        Assert.Equal(rate, UsageCredits.FindRate($"{model}-2026-10-{day:00}", release, null));
        Assert.Equal(rate, UsageCredits.FindRate($"{model}-202610{day:00}", release, null));
        Assert.Equal(rate, UsageCredits.FindBuiltInByPricing(rate with { Source = null, CatalogVersion = null }));
    }

    [Fact]
    public void Sonnet55_CachePriceChangesOnOctober7WithoutRepricingSeptember()
    {
        var cache = new TokenBreakdown(1_000_000, 1_000_000, 0, 0, 1_000_000);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(new DateOnly(2026, 9, 28), "claude-sonnet-5-5", cache),
            new DatedModelTokenUsage(new DateOnly(2026, 10, 6), "claude-sonnet-5-5", cache),
            new DatedModelTokenUsage(new DateOnly(2026, 10, 7), "claude-sonnet-5-5", cache)
        ]);

        Assert.Equal(0, result.UnratedTokens);
        Assert.Equal(0.5, UsageCredits.ToAmount(result.CreditsUsed), 8);
        var rows = Assert.Single(result.ByModel).RateVersions;
        Assert.Equal(2, rows.Count);
        Assert.Equal("anthropic-2026.10.1", rows[0].CatalogVersion);
        Assert.Equal(new DateOnly(2026, 9, 28), rows[0].EffectiveFrom);
        Assert.Equal(10, rows[0].CachedInputCredits);
        Assert.Equal("anthropic-2026.11.1", rows[1].CatalogVersion);
        Assert.Equal(new DateOnly(2026, 10, 7), rows[1].EffectiveFrom);
        Assert.Equal(2.5, rows[1].CachedInputCredits);
    }

    [Fact]
    public void Haiku55_UsesBaseCacheWritePricesForTheShortContextEquivalent()
    {
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(new DateOnly(2026, 10, 7), "claude-haiku-5-5",
                new TokenBreakdown(2_000_000, 0, 0, 0, 2_000_000, 1_000_000, 1_000_000))
        ]);

        var priced = Assert.Single(result.ByModel);
        Assert.Equal(0, priced.InputCredits);
        Assert.Equal(0.325, UsageCredits.ToAmount(priced.CacheWriteCredits), 8);
        Assert.Contains("短上下文", Assert.Single(priced.RateVersions).Source, StringComparison.Ordinal);
    }

    [Fact]
    public void OldPinnedCatalog_PreservesSonnetPriceAndDoesNotAcquireNewModels()
    {
        ModelCreditRate[] pinned =
        [
            new("gpt-6.1-sol", 50, 2.5, 250, new DateOnly(2026, 9, 29), "old snapshot", "2026.10.1"),
            new("claude-sonnet-5-5", 50, 5, 250, new DateOnly(2026, 9, 28), "old snapshot", "anthropic-2026.10.1")
        ];
        var date = new DateOnly(2026, 11, 1);
        var cache = new TokenBreakdown(1_000_000, 1_000_000, 0, 0, 1_000_000);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(date, "claude-sonnet-5-5", cache),
            new DatedModelTokenUsage(date, "claude-haiku-5-5", cache),
            new DatedModelTokenUsage(date, "gpt-6.1-terra", cache),
            new DatedModelTokenUsage(date, "gpt-6-rosalind", cache)
        ], pinned, completeRateCatalog: true);

        Assert.Equal(0.2, UsageCredits.ToAmount(result.CreditsUsed), 8);
        Assert.Equal(3_000_000, result.UnratedTokens);
        Assert.Equal("claude-sonnet-5-5", Assert.Single(result.ByModel).Model);
        var exported = UsageCredits.CreateCatalogDocument(pinned, completeSnapshot: true,
            catalogVersion: "old snapshot", source: "old snapshot", baseCatalogVersion: "2026.10.1");
        Assert.Equal(2, exported.Rates.Count);
        Assert.Equal("2026.10.1", exported.BaseCatalogVersion);
    }

    [Theory]
    [InlineData("gpt-6.1-terra")]
    [InlineData("gpt-6-rosalind")]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-sonnet-5-5")]
    public void CustomRate_RemainsAuthoritativeAfterCatalogUpdate(string model)
    {
        var custom = new ModelCreditRate(model, 10, 2, 30, Source: "my vendor", CatalogVersion: "my rates");
        var date = new DateOnly(2026, 11, 1);
        Assert.Same(custom, UsageCredits.FindRate(model, date, [custom]));
        var exported = UsageCredits.CreateCatalogDocument([custom]);
        Assert.Equal(custom, Assert.Single(exported.Rates, row => row.Model == model));
    }

    [Theory]
    [InlineData("gpt-6.1-terra-codex")]
    [InlineData("gpt-6.1-terra-ultrafast")]
    [InlineData("gpt-6-rosalind-preview")]
    [InlineData("claude-haiku-5-5-unannounced")]
    public void UnknownVariants_DoNotBorrowPrices(string model)
    {
        Assert.Null(UsageCredits.FindRate(model, new DateOnly(2026, 11, 1), null));
    }
}
