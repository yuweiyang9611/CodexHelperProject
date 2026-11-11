namespace CodexU.Core;

public enum SubscriptionSource { Unknown, Auto, Manual }

public enum PaybackStatus { Unknown, Below, Reached }

public sealed record MonthlyValue(
    double KnownAmount,
    bool IsLowerBound,
    double? SubscriptionAmount,
    SubscriptionSource SubscriptionSource,
    double? NetAmount,
    double? PaybackMultiple,
    PaybackStatus PaybackStatus,
    double? ProjectedAmount,
    double? CoveragePercent)
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

        var known = UsageCredits.ToAmount(month.CreditsUsed, settings.AmountPerThousandCredits);
        var partial = month.UnratedTokens > 0;
        var available = month.Quality != DataQuality.Unavailable;
        var status = PaybackStatus.Unknown;
        if (available && subscription is { } fee)
        {
            if (known >= fee) status = PaybackStatus.Reached;
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
                : null);
    }
}
