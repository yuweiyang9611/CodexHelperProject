namespace CodexU.Core;

public enum SubscriptionSource { Unknown, Auto, Manual }

public enum PaybackStatus { Unknown, Below, Reached }

public sealed record MonthlyCostBreakdown(double InputAndWrites, double CachedInput, double Output, double CachedSavings);

public sealed record ModelCostAmount(string Model, double Input, double CachedInput, double CacheWrite,
    double Output, double CachedSavings, double Total, IReadOnlyList<RateVersionAmount> RateVersions);

public sealed record RateVersionAmount(string CatalogVersion, string Source, DateOnly? EffectiveFrom, double Total);

public sealed record MonthlyValue(
    double? KnownAmount,
    bool IsLowerBound,
    double? SubscriptionAmount,
    SubscriptionSource SubscriptionSource,
    double? NetAmount,
    double? PaybackMultiple,
    PaybackStatus PaybackStatus,
    double? ProjectedAmount,
    double? CoveragePercent,
    MonthlyCostBreakdown? Breakdown,
    IReadOnlyList<ModelCostAmount> Models)
{
    public static MonthlyValue Calculate(DashboardSnapshot snapshot, AppSettings settings)
    {
        var month = snapshot.Tokens.Month;
        var automatic = settings.AutoDetectSubscriptionAmountFor(snapshot.Runtime);
        var subscription = automatic
            ? snapshot.Account?.SuggestedMonthlySubscriptionAmount
            : settings.ManualSubscriptionAmountFor(snapshot.Runtime);
        var source = subscription is null ? SubscriptionSource.Unknown
            : automatic ? SubscriptionSource.Auto : SubscriptionSource.Manual;

        double Dollars(double credits) => UsageCredits.ToAmount(credits, settings.AmountPerThousandCredits);
        var amount = UsageAmount.FromPeriod(month, settings.AmountPerThousandCredits);
        var known = amount.KnownAmount;
        var partial = amount.IsLowerBound;
        var available = month.Quality != DataQuality.Unavailable;
        var models = month.CreditsByModel.Select(model => new ModelCostAmount(model.Model,
            Dollars(model.InputCredits), Dollars(model.CachedInputCredits), Dollars(model.CacheWriteCredits),
            Dollars(model.OutputCredits), Dollars(model.CachedSavingsCredits), Dollars(model.TotalCredits),
            model.RateVersions.Select(version => new RateVersionAmount(version.CatalogVersion, version.Source,
                version.EffectiveFrom, Dollars(version.TotalCredits))).ToArray())).ToArray();
        // A zero-rate row is evidence for a zero cost. A month with only unpriced tokens has no such evidence.
        var breakdown = known.HasValue && (models.Length > 0 || month.Tokens == 0 && known == 0)
            ? new MonthlyCostBreakdown(models.Sum(m => m.Input + m.CacheWrite), models.Sum(m => m.CachedInput),
                models.Sum(m => m.Output), models.Sum(m => m.CachedSavings))
            : null;
        var status = PaybackStatus.Unknown;
        if (known is { } total && subscription is { } fee)
        {
            if (total >= fee) status = PaybackStatus.Reached;
            else if (!partial) status = PaybackStatus.Below;
        }

        var comparable = status != PaybackStatus.Unknown;
        var local = snapshot.RefreshedAt.LocalDateTime;
        var elapsedDays = Math.Max(1d / 24, local.Day - 1 + local.TimeOfDay.TotalDays);
        return new(known, partial, subscription, source,
            comparable ? known - subscription : null,
            comparable && subscription > 0 ? known / subscription : null,
            status,
            available && !partial ? known * DateTime.DaysInMonth(local.Year, local.Month) / elapsedDays : null,
            available && month.Tokens > 0
                ? 100d * (month.Tokens - Math.Clamp(month.UnratedTokens, 0, month.Tokens)) / month.Tokens
                : null,
            breakdown, models);
    }
}
