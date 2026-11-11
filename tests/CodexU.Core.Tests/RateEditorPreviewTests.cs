using System.Text.Json;
using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class RateEditorPreviewTests
{
    private static readonly DateOnly Today = new(2026, 11, 11);

    [Fact]
    public void PreviewUsesTheSameEffectiveRateAsSavingTheDraft()
    {
        var draft = new ModelCreditRate(" GPT-5.2-CODEX-LATEST ", 91, 9, 900, new(2026, 10, 9), " custom ", " v1 ");
        const string model = "gpt-5.2-20261101";
        var preview = RateEditorPreview.Evaluate(new([draft], model), false, Today);
        var saved = new AppSettings(CustomModelRates: [draft]).Validate().Normalize();
        Assert.Null(preview.ValidationMessage);
        Assert.Equal(1, preview.CustomOverrideCount);
        Assert.Equal(UsageCredits.FindRate(model, Today, saved.CustomModelRates), preview.EffectiveRate);
        Assert.Equal("custom", preview.EffectiveRate!.Source);
        Assert.NotEqual(preview.EffectiveRate, preview.BuiltInRate);

        var historical = RateEditorPreview.Evaluate(new([draft], model, new(2026, 10, 8)), false, Today);
        Assert.Equal(historical.BuiltInRate, historical.EffectiveRate);
    }

    [Fact]
    public void DateSpecificSeedAndPreviewHonorRealPriceChangesAndPinnedScope()
    {
        var old = RateEditorPreview.Evaluate(new([], "claude-sonnet-5-5", new(2026, 10, 6)), false, Today);
        var current = RateEditorPreview.Evaluate(new([], "claude-sonnet-5-5", new(2026, 10, 7)), false, Today);
        Assert.Equal(5d, old.BuiltInRate!.CachedInputCreditsPerMillion);
        Assert.Equal(2.5, current.BuiltInRate!.CachedInputCreditsPerMillion);
        var pinned = RateEditorPreview.Evaluate(new([new("gpt-5.4", 1, 1, 1)], "gpt-5.2"), true, Today);
        Assert.NotNull(pinned.BuiltInRate);
        Assert.Null(pinned.EffectiveRate);
        Assert.False(pinned.CanAdd);
    }

    [Fact]
    public void NextRowSkipsFamiliesAlreadyOverriddenAndUsesCurrentPrice()
    {
        var preview = RateEditorPreview.Evaluate(new([new("claude", 1, 2, 3, MatchMode: "prefix")]), false, Today);
        var next = Assert.IsType<ModelCreditRate>(preview.NextRate);
        Assert.DoesNotContain("claude", next.Model);
        Assert.Equal(Today, next.EffectiveFrom);
        Assert.Equal(UsageCredits.FindRate(next.Model, Today)!.InputCreditsPerMillion, next.InputCreditsPerMillion);
        var all = UsageCredits.BuiltInRates;
        var complete = RateEditorPreview.Evaluate(new(all), false, Today);
        Assert.True(complete.CanAdd);
        Assert.Equal("", complete.NextRate!.Model);
        Assert.Equal(0, complete.CustomOverrideCount);
    }

    [Theory]
    [InlineData("""{"rates":[{"model":"gpt-5.2"}]}""")]
    [InlineData("""{"rates":[{"model":"gpt-5.2","inputCreditsPerMillion":1,"cachedInputCreditsPerMillion":0,"outputCreditsPerMillion":1,"effectiveFrom":"2026-02-31"}]}""")]
    [InlineData("""{"rates":[{"model":"gpt-5.2","inputCreditsPerMillion":"","cachedInputCreditsPerMillion":0,"outputCreditsPerMillion":1}]}""")]
    public void UnfilledOrMalformedFieldsReturnValidationWithoutSeeding(string json)
    {
        using var data = JsonDocument.Parse(json);
        var preview = RateEditorPreview.FromPayload(data.RootElement, false);
        Assert.NotNull(preview.ValidationMessage);
        Assert.False(preview.CanAdd);
    }

    [Fact]
    public void DuplicateNormalizedKeysAreRejectedJustAsTheyAreOnSave()
    {
        ModelCreditRate[] rates = [new("gpt-5.2", 1, 2, 3), new("gpt-5.2-codex-latest", 4, 5, 6)];
        var error = Assert.Throws<ArgumentException>(() => new AppSettings(CustomModelRates: rates).Validate());
        var preview = RateEditorPreview.Evaluate(new(rates), false, Today);
        Assert.NotNull(preview.ValidationMessage);
        Assert.StartsWith(preview.ValidationMessage, error.Message);
        Assert.Null(preview.EffectiveRate);
    }
}
