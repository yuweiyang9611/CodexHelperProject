using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class LateNovemberCatalogUpdateTests
{
    public static TheoryData<string, int, double, double, double, string> NewModels => new()
    {
        { "gpt-6.2-astra", 17, 8, 0.4, 40, "2026.11.2" },
        { "gpt-6.2-sol", 17, 2, 0.1, 10, "2026.11.2" },
        { "claude-fable-5-2", 9, 8, 0.2, 40, "anthropic-2026.11.2" },
        { "claude-mythos-5-2", 9, 8, 0.2, 40, "anthropic-2026.11.2" }
    };

    [Theory]
    [MemberData(nameof(NewModels))]
    public void NewModels_PriceOnlyFromReleaseAndPreserveProvenance(
        string model, int day, double input, double cached, double output, string version)
    {
        var release = new DateOnly(2026, 11, day);
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
        Assert.Equal(rate, UsageCredits.FindRate($"{model}-2026-11-{day:00}", release, null));
        Assert.Equal(rate, UsageCredits.FindBuiltInByPricing(rate with { Source = null, CatalogVersion = null }));
    }

    [Fact]
    public void ThisMonthsMixOfNewModels_IsFullyPricedAtTheOfficialRates()
    {
        // 1M uncached input, 1M cache reads and 1M output per model.
        var tokens = new TokenBreakdown(2_000_000, 1_000_000, 1_000_000, 0, 3_000_000);
        var date = new DateOnly(2026, 11, 27);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(date, "gpt-6.2-astra", tokens),
            new DatedModelTokenUsage(date, "gpt-6.2-sol", tokens),
            new DatedModelTokenUsage(date, "claude-fable-5-2", tokens)
        ]);

        Assert.Equal(0, result.UnratedTokens);
        Assert.Equal((8 + 0.4 + 40) + (2 + 0.1 + 10) + (8 + 0.2 + 40), UsageCredits.ToAmount(result.CreditsUsed), 8);
    }

    [Fact]
    public void CodexAutoReview_IsCountedAsFreeOnEveryDateInsteadOfUnrated()
    {
        var tokens = new TokenBreakdown(2_000_000, 1_000_000, 1_000_000, 0, 3_000_000);
        var result = UsageCredits.Calculate(
        [
            new DatedModelTokenUsage(new DateOnly(2026, 7, 1), "codex-auto-review", tokens),
            new DatedModelTokenUsage(new DateOnly(2026, 11, 27), "codex-auto-review", tokens)
        ]);

        Assert.Equal(0, result.UnratedTokens);
        Assert.Equal(0, result.CreditsUsed);
        var priced = Assert.Single(result.ByModel);
        Assert.Equal(6_000_000, priced.Tokens.TotalTokens);
        Assert.Contains("ChatGPT", Assert.Single(priced.RateVersions).Source, StringComparison.Ordinal);
        // An exact row: a differently named review model never inherits it.
        Assert.Null(UsageCredits.FindRate("codex-auto-review-pro", new DateOnly(2026, 11, 27), null));
    }

    [Theory]
    // The healthcare configurations and Ultrafast are separate API models or tiers.
    [InlineData("gpt-6.2-astra-healthcare")]
    [InlineData("gpt-6.2-sol-healthcare")]
    [InlineData("gpt-6.2-sol-ultrafast")]
    public void ModelsWithoutAnOfficialRate_DoNotBorrowAPrice(string model)
    {
        Assert.Null(UsageCredits.FindRate(model, new DateOnly(2026, 11, 27), null));
    }

    [Fact]
    public void CatalogStillExcludesFable51FromTheNewRow()
    {
        var fable51 = Assert.IsType<ModelCreditRate>(UsageCredits.FindRate("claude-fable-5-1", new DateOnly(2026, 11, 27), null));
        Assert.Equal(250, fable51.InputCreditsPerMillion);
    }
}
