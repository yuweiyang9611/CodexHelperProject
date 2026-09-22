using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class PersonalUsageSettingsTests
{
    [Fact]
    public void Defaults_OnlyEnableQuotaLowAndRefreshedNotifications()
    {
        var settings = new AppSettings().Normalize();

        Assert.True(settings.NotificationsEnabled);
        Assert.False(settings.QuotaForecastAlertsEnabled);
        Assert.Equal(0, settings.MonthlyAmountAlert);
        Assert.Equal(0, settings.MinimumRateCoverageAlertPercent);
    }

    [Fact]
    public void Normalize_RetiresDesktopReplicaAndTokenStrip_ButKeepsExplicitNotificationChoices()
    {
        var settings = new AppSettings(
            DesktopMode: true,
            StatusStripEnabled: true,
            StatusStripShowTodayTokens: true,
            StatusStripQuotaMode: "used",
            QuotaForecastAlertsEnabled: true,
            MonthlyAmountAlert: 100,
            MinimumRateCoverageAlertPercent: 70).Normalize();

        Assert.False(settings.DesktopMode);
        Assert.True(settings.StatusStripEnabled);
        Assert.False(settings.StatusStripShowTodayTokens);
        Assert.Equal("remaining", settings.StatusStripQuotaMode);
        Assert.True(settings.QuotaForecastAlertsEnabled);
        Assert.Equal(100, settings.MonthlyAmountAlert);
        Assert.Equal(70, settings.MinimumRateCoverageAlertPercent);
    }
}
