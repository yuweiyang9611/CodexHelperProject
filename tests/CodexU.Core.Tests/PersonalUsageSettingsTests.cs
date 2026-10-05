using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class PersonalUsageSettingsTests
{
    [Fact]
    public void Defaults_RestoreVersion060NotificationAndStatusStripOptions()
    {
        var settings = new AppSettings().Normalize();

        Assert.True(settings.NotificationsEnabled);
        Assert.True(settings.QuotaForecastAlertsEnabled);
        Assert.Equal(0, settings.MonthlyAmountAlert);
        Assert.Equal(80, settings.MinimumRateCoverageAlertPercent);
        Assert.True(settings.StatusStripShowTodayTokens);
        Assert.Equal("remaining", settings.StatusStripQuotaMode);
        Assert.False(settings.DesktopMode);
    }

    [Fact]
    public void Normalize_PreservesDesktopAndStatusStripWithExplicitNotificationChoices()
    {
        var settings = new AppSettings(
            DesktopMode: true,
            StatusStripEnabled: true,
            StatusStripShowTodayTokens: true,
            StatusStripQuotaMode: "USED",
            QuotaForecastAlertsEnabled: true,
            MonthlyAmountAlert: 100,
            MinimumRateCoverageAlertPercent: 70).Normalize();

        Assert.True(settings.DesktopMode);
        Assert.True(settings.StatusStripEnabled);
        Assert.True(settings.StatusStripShowTodayTokens);
        Assert.Equal("used", settings.StatusStripQuotaMode);
        Assert.True(settings.QuotaForecastAlertsEnabled);
        Assert.Equal(100, settings.MonthlyAmountAlert);
        Assert.Equal(70, settings.MinimumRateCoverageAlertPercent);
    }

    [Fact]
    public void Normalize_DoesNotReplaceExplicitDisabledOptionsWithRestoredDefaults()
    {
        var settings = new AppSettings(
            DesktopMode: false,
            StatusStripShowTodayTokens: false,
            QuotaForecastAlertsEnabled: false,
            MinimumRateCoverageAlertPercent: 0).Normalize();

        Assert.False(settings.DesktopMode);
        Assert.False(settings.StatusStripShowTodayTokens);
        Assert.False(settings.QuotaForecastAlertsEnabled);
        Assert.Equal(0, settings.MinimumRateCoverageAlertPercent);
    }

    [Theory]
    [InlineData("remaining")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalize_UnknownQuotaModeFallsBackToRemaining(string? quotaMode)
    {
        var settings = new AppSettings(StatusStripQuotaMode: quotaMode!).Normalize();

        Assert.Equal("remaining", settings.StatusStripQuotaMode);
    }
}
