using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class QuotaForecasterTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Project_MeasuresTheRateAcrossTheWholeLookbackNotTheLatestPair()
    {
        // The default refresh is five minutes, so a rule that required the newest
        // PAIR to span the minimum would never be satisfied and the forecast would
        // silently never appear. Anchoring on the oldest reading in the lookback is
        // what makes a fifteen-minute minimum reachable at a five-minute cadence.
        var samples = Series((0, 10d), (5, 15d), (10, 20d), (15, 25d), (20, 30d));

        var forecast = QuotaForecaster.Project(Quota(30, resetsIn: TimeSpan.FromHours(5)), samples, Now);

        Assert.NotNull(forecast);
        Assert.Equal(TimeSpan.FromMinutes(20), forecast.MeasuredOver);
        Assert.Equal(1d, forecast.PercentPerMinute, precision: 6);
        // 70% left at 1%/min.
        Assert.Equal(70, forecast.TimeToExhaustion.TotalMinutes, precision: 6);
    }

    [Fact]
    public void Project_WithdrawsRatherThanGuessWhenHistoryIsTooShort()
    {
        var samples = Series((0, 10d), (5, 40d));

        Assert.Null(QuotaForecaster.Project(Quota(40), samples, Now));
    }

    [Fact]
    public void Project_IgnoresReadingsFromBeforeTheWindowReset()
    {
        // A reset shows up as a fall in used percent. Readings from the previous
        // window instance describe a quota that no longer exists.
        var samples = Series((0, 80d), (5, 90d), (10, 4d), (25, 10d), (40, 16d));

        var forecast = QuotaForecaster.Project(Quota(16, resetsIn: TimeSpan.FromHours(4)), samples, Now);

        Assert.NotNull(forecast);
        // Only the three post-reset readings count: 30 minutes for 12%.
        Assert.Equal(TimeSpan.FromMinutes(30), forecast.MeasuredOver);
        Assert.Equal(0.4d, forecast.PercentPerMinute, precision: 6);
    }

    [Fact]
    public void Project_DampsABurstByAveragingOverTheLookback()
    {
        // A quiet stretch followed by one busy interval. Measuring the last pair
        // alone would report 4%/min and a frightening projection; across the
        // lookback the same burst is 1%/min.
        var samples = Series((0, 10d), (20, 12d), (40, 14d), (50, 34d));

        var forecast = QuotaForecaster.Project(Quota(34, resetsIn: TimeSpan.FromHours(5)), samples, Now);

        Assert.NotNull(forecast);
        Assert.Equal(24d / 50d, forecast.PercentPerMinute, precision: 6);
        var latestPairRate = (34d - 14d) / 10d;
        Assert.True(forecast.PercentPerMinute < latestPairRate / 4);
    }

    [Fact]
    public void Project_SaysNothingWhenUsageIsFlat()
    {
        var samples = Series((0, 42d), (10, 42d), (20, 42d));

        Assert.Null(QuotaForecaster.Project(Quota(42), samples, Now));
    }

    [Fact]
    public void Project_SaysNothingOnAnAlreadyExhaustedWindow()
    {
        var samples = Series((0, 80d), (20, 100d));

        Assert.Null(QuotaForecaster.Project(Quota(100), samples, Now));
    }

    [Fact]
    public void Project_FlagsAWindowThatResetsBeforeItWouldRunOut()
    {
        // Burning steadily but the window rolls over first — nothing worth alerting
        // on, however fast the consumption looks.
        var samples = Series((0, 10d), (20, 20d), (40, 30d));

        var forecast = QuotaForecaster.Project(Quota(30, resetsIn: TimeSpan.FromMinutes(30)), samples, Now);

        Assert.NotNull(forecast);
        // 70% left at 0.5%/min is 140 minutes, well past the 30-minute reset.
        Assert.Equal(140, forecast.TimeToExhaustion.TotalMinutes, precision: 6);
        Assert.False(forecast.ExhaustsBeforeReset);
    }

    [Fact]
    public void Project_FlagsAWindowThatRunsOutFirst()
    {
        var samples = Series((0, 10d), (20, 40d), (40, 70d));

        var forecast = QuotaForecaster.Project(Quota(70, resetsIn: TimeSpan.FromHours(3)), samples, Now);

        Assert.NotNull(forecast);
        Assert.Equal(20, forecast.TimeToExhaustion.TotalMinutes, precision: 6);
        Assert.True(forecast.ExhaustsBeforeReset);
    }

    [Fact]
    public void Project_DropsReadingsOlderThanTheLookback()
    {
        // A stale reading from hours ago must not flatten the current slope.
        var samples = Series((-600, 0d), (0, 50d), (20, 70d));

        var forecast = QuotaForecaster.Project(Quota(70, resetsIn: TimeSpan.FromHours(2)), samples, Now);

        Assert.NotNull(forecast);
        Assert.Equal(TimeSpan.FromMinutes(20), forecast.MeasuredOver);
        Assert.Equal(1d, forecast.PercentPerMinute, precision: 6);
    }

    [Fact]
    public void Project_NeedsAQuotaAndAtLeastTwoReadings()
    {
        Assert.Null(QuotaForecaster.Project(null, Series((0, 10d), (20, 30d)), Now));
        Assert.Null(QuotaForecaster.Project(Quota(30), Series((0, 10d)), Now));
        Assert.Null(QuotaForecaster.Project(Quota(30), [], Now));
    }

    [Fact]
    public void ShouldWarn_FiresWhenAWindowWillRunOutSoonAndBeforeItResets()
    {
        Assert.True(QuotaForecastAlert.ShouldWarn(
            Quota(60, resetsIn: TimeSpan.FromHours(3)),
            Forecast(TimeSpan.FromMinutes(40), exhaustsBeforeReset: true),
            alertThresholdPercent: 20,
            QuotaForecastAlert.PrimaryLeadTime));
    }

    [Fact]
    public void ShouldWarn_StaysQuietWhenTheWindowResetsFirst()
    {
        Assert.False(QuotaForecastAlert.ShouldWarn(
            Quota(60, resetsIn: TimeSpan.FromMinutes(20)),
            Forecast(TimeSpan.FromMinutes(40), exhaustsBeforeReset: false),
            alertThresholdPercent: 20,
            QuotaForecastAlert.PrimaryLeadTime));
    }

    [Fact]
    public void ShouldWarn_StaysQuietWhileExhaustionIsStillFarOff()
    {
        // True but not yet actionable. Warning four hours out trains the user to
        // ignore the warning.
        Assert.False(QuotaForecastAlert.ShouldWarn(
            Quota(30, resetsIn: TimeSpan.FromHours(5)),
            Forecast(TimeSpan.FromHours(4), exhaustsBeforeReset: true),
            alertThresholdPercent: 20,
            QuotaForecastAlert.PrimaryLeadTime));
    }

    [Fact]
    public void ShouldWarn_YieldsToThePlainAlertOnceRemainingIsBelowTheThreshold()
    {
        // At 15% left the remaining-percent alert has already fired. A second balloon
        // restating it in different words is noise, not information.
        Assert.False(QuotaForecastAlert.ShouldWarn(
            Quota(85, resetsIn: TimeSpan.FromHours(3)),
            Forecast(TimeSpan.FromMinutes(20), exhaustsBeforeReset: true),
            alertThresholdPercent: 20,
            QuotaForecastAlert.PrimaryLeadTime));
    }

    [Fact]
    public void ShouldWarn_StaysQuietWithoutAQuotaOrAForecast()
    {
        var forecast = Forecast(TimeSpan.FromMinutes(20), exhaustsBeforeReset: true);
        Assert.False(QuotaForecastAlert.ShouldWarn(null, forecast, 20, QuotaForecastAlert.PrimaryLeadTime));
        Assert.False(QuotaForecastAlert.ShouldWarn(Quota(60), null, 20, QuotaForecastAlert.PrimaryLeadTime));
    }

    [Fact]
    public void ShouldWarn_GivesTheWeeklyWindowAMuchLongerLeadTime()
    {
        // Twelve hours out is far too early to interrupt over a five-hour window but
        // is the right moment to hear about a week's allowance, where the response is
        // to rearrange several days of work.
        var forecast = Forecast(TimeSpan.FromHours(12), exhaustsBeforeReset: true);
        var quota = Quota(60, resetsIn: TimeSpan.FromDays(3));

        Assert.False(QuotaForecastAlert.ShouldWarn(quota, forecast, 20, QuotaForecastAlert.PrimaryLeadTime));
        Assert.True(QuotaForecastAlert.ShouldWarn(quota, forecast, 20, QuotaForecastAlert.SecondaryLeadTime));
    }

    private static QuotaForecast Forecast(TimeSpan timeToExhaustion, bool exhaustsBeforeReset) => new(
        PercentPerMinute: 1d,
        TimeToExhaustion: timeToExhaustion,
        ExhaustsAt: Now + timeToExhaustion,
        ExhaustsBeforeReset: exhaustsBeforeReset,
        MeasuredOver: TimeSpan.FromMinutes(20));

    /// <summary>Readings at minute offsets ending at <see cref="Now"/>.</summary>
    private static QuotaSample[] Series(params (int MinutesBeforeEnd, double UsedPercent)[] points)
    {
        var last = points[^1].MinutesBeforeEnd;
        return points
            .Select(point => new QuotaSample(
                Now.AddMinutes(point.MinutesBeforeEnd - last),
                point.UsedPercent,
                null))
            .ToArray();
    }

    private static RateLimitWindow Quota(double usedPercent, TimeSpan? resetsIn = null) =>
        new(usedPercent, 300, resetsIn is null ? null : Now + resetsIn.Value);
}
