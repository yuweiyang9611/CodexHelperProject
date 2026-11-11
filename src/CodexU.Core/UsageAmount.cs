namespace CodexU.Core;

/// <summary>USD equivalent of the priced part of a local usage measurement.</summary>
public sealed record UsageAmount(double? KnownAmount, bool IsLowerBound)
{
    public static UsageAmount FromPeriod(TokenPeriod period, double conversion) =>
        FromPeriod(period.Tokens, period.CreditsUsed, period.UnratedTokens, period.Quality, conversion);

    internal static UsageAmount FromPeriod(long tokens, double credits, long unrated, DataQuality quality, double conversion)
    {
        if (quality == DataQuality.Unavailable || (tokens > 0 && unrated >= tokens))
            return new(null, false);
        return new(UsageCredits.ToAmount(credits, conversion), unrated > 0);
    }
}
