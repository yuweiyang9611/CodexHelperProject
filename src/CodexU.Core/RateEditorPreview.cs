using System.Text.Json;

namespace CodexU.Core;

public sealed record RateDraftRequest(IReadOnlyList<ModelCreditRate> Rates, string? Model = null, DateOnly? EffectiveOn = null);

public sealed record RateEditorPreview(
    string? ValidationMessage, int CustomOverrideCount, int VersionCount,
    bool CanAdd, ModelCreditRate? NextRate, ModelCreditRate? BuiltInRate, ModelCreditRate? EffectiveRate)
{
    private static readonly JsonSerializerOptions DraftOptions = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };

    public static RateEditorPreview FromPayload(JsonElement payload, bool pinned)
    {
        try
        {
            var request = payload.Deserialize<RateDraftRequest>(DraftOptions);
            return request?.Rates is null ? Invalid("缺少费率草稿。")
                : Evaluate(request, pinned, DateOnly.FromDateTime(DateTime.Today));
        }
        catch (JsonException)
        {
            return Invalid("请填写有效数值和有效的生效日期。");
        }
    }

    public static RateEditorPreview Evaluate(RateDraftRequest request, bool pinned, DateOnly today)
    {
        var rates = request.Rates;
        if (AppSettings.CustomRateError(rates, pinned) is { } error) return Invalid(error);

        rates = AppSettings.NormalizeCustomRates(rates, pinned);
        var overrides = rates.Count(r => !UsageCredits.IsBuiltInRate(r));
        var canAdd = !pinned && overrides < UsageCredits.MaximumCustomRateCount && rates.Count < UsageCredits.MaximumCatalogRateCount;
        ModelCreditRate? next = null;
        if (canAdd)
        {
            var model = UsageCredits.BuiltInRates.Where(r => r.EffectiveFrom is null || r.EffectiveFrom <= today)
                .Select(r => r.Model).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .FirstOrDefault(name => !rates.Any(r => UsageCredits.MatchesModel(
                    UsageCredits.NormalizeModel(name), UsageCredits.NormalizeRatePattern(name, "prefix"), r, true))) ?? "";
            var seed = UsageCredits.FindRate(model, today);
            next = new(model, seed?.InputCreditsPerMillion ?? 0, seed?.CachedInputCreditsPerMillion ?? 0,
                seed?.OutputCreditsPerMillion ?? 0, today, "用户自定义", $"custom-{today:yyyy.MM.dd}", "exact");
        }
        var date = request.EffectiveOn ?? today;
        return new(null, overrides, rates.Select(r => r.CatalogVersion ?? "custom").Distinct().Count(), canAdd, next,
            string.IsNullOrWhiteSpace(request.Model) ? null : UsageCredits.FindRate(request.Model, date),
            string.IsNullOrWhiteSpace(request.Model) ? null : UsageCredits.FindRate(request.Model, date, rates, pinned));
    }

    private static RateEditorPreview Invalid(string message) => new(message, 0, 0, false, null, null, null);
}
