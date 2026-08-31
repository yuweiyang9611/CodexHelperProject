using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class QuotaNotificationProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Project_AnnouncesAThresholdOnceAndReArmsAfterWindowRollover()
    {
        var projector = Projector();
        var settings = Settings();
        var low = Snapshot(primary: Window(remainingPercent: 10));

        var first = Assert.Single(projector.Project(low, settings));
        Assert.StartsWith("quota-below-threshold:", first.Id);
        Assert.Equal("codexU 额度提醒", first.Title);
        Assert.Contains("5 小时额度剩余 10%", first.Body);
        Assert.Empty(projector.Project(low, settings));

        Assert.Empty(projector.Project(
            Snapshot(primary: Window(remainingPercent: 98)),
            settings));
        Assert.Single(projector.Project(low, settings));
    }

    [Fact]
    public void Project_DoesNotConsumeAnAlertWhileNotificationsAreDisabled()
    {
        var projector = Projector();
        var snapshot = Snapshot(primary: Window(remainingPercent: 5));

        Assert.Empty(projector.Project(
            snapshot,
            Settings() with { NotificationsEnabled = false }));
        Assert.Single(projector.Project(snapshot, Settings()));
    }

    [Fact]
    public void Project_ObservesRolloverWhileNotificationsAreDisabled()
    {
        var projector = Projector();
        var settings = Settings();
        var low = Snapshot(primary: Window(remainingPercent: 5));

        Assert.Single(projector.Project(low, settings));
        Assert.Empty(projector.Project(
            Snapshot(primary: Window(remainingPercent: 98)),
            settings with { NotificationsEnabled = false }));

        Assert.Single(projector.Project(low, settings));
    }

    [Fact]
    public void Project_AnnouncesForecastOnlyWhenEnabledAndActionable()
    {
        var projector = Projector();
        var quota = Window(remainingPercent: 50, resetsIn: TimeSpan.FromHours(4));
        var forecast = new QuotaForecast(
            PercentPerMinute: 1,
            TimeToExhaustion: TimeSpan.FromMinutes(30),
            ExhaustsAt: Now.AddMinutes(30),
            ExhaustsBeforeReset: true,
            MeasuredOver: TimeSpan.FromMinutes(45));
        var snapshot = Snapshot(primary: quota, primaryForecast: forecast);

        Assert.Empty(projector.Project(
            snapshot,
            Settings() with { QuotaForecastAlertsEnabled = false }));
        var notification = Assert.Single(projector.Project(snapshot, Settings()));
        Assert.StartsWith("quota-forecast:", notification.Id);
        Assert.Equal("codexU 额度预警", notification.Title);
        Assert.Contains("30 分钟耗尽", notification.Body);
        Assert.Empty(projector.Project(snapshot, Settings()));
    }

    [Fact]
    public void Project_DistinguishesARefreshFromAResetScheduleChange()
    {
        var projector = Projector();
        var settings = Settings();

        Assert.Empty(projector.Project(
            Snapshot(primary: Window(remainingPercent: 10, resetsIn: TimeSpan.FromHours(1))),
            settings with { FiveHourAlertPercent = 5 }));

        var refreshed = Assert.Single(projector.Project(
            Snapshot(primary: Window(remainingPercent: 98, resetsIn: TimeSpan.FromHours(5))),
            settings));
        Assert.StartsWith("quota-refreshed:", refreshed.Id);
        Assert.Equal("codexU 额度已刷新", refreshed.Title);

        var rescheduled = Assert.Single(projector.Project(
            Snapshot(primary: Window(remainingPercent: 90, resetsIn: TimeSpan.FromHours(6))),
            settings));
        Assert.StartsWith("quota-rescheduled:", rescheduled.Id);
        Assert.Equal("codexU 刷新时间已更新", rescheduled.Title);
    }

    [Fact]
    public void Project_DeduplicatesMonthlyAmountAndRateCoveragePerCalendarMonth()
    {
        var now = Now;
        var projector = new QuotaNotificationProjector(() => now);
        var month = new TokenPeriod(
            Tokens: 1_000,
            Breakdown: TokenBreakdown.Zero,
            CreditsUsed: 1_000,
            UnratedTokens: 500,
            CreditsByModel: [],
            Quality: DataQuality.Partial);
        var snapshot = Snapshot(month: month);
        var settings = Settings() with
        {
            MonthlyAmountAlert = 20,
            MinimumRateCoverageAlertPercent = 80
        };

        var first = projector.Project(snapshot, settings);
        Assert.Equal(2, first.Count);
        Assert.Contains(first, item => item.Title == "codexU 本月金额提醒");
        Assert.Contains(first, item => item.Title == "codexU 费率覆盖提醒");
        Assert.Equal(first.Count, first.Select(item => item.Id).Distinct().Count());
        Assert.Empty(projector.Project(snapshot, settings));

        now = now.AddMonths(1);
        Assert.Equal(2, projector.Project(snapshot, settings).Count);
    }

    [Fact]
    public void Project_UsesStableLogicalIdsAcrossProjectorRestarts()
    {
        var settings = Settings();
        var firstSnapshot = Snapshot(
            primary: Window(remainingPercent: 10, resetsIn: TimeSpan.FromHours(2)));
        var changedReadingInSameWindow = Snapshot(
            primary: Window(remainingPercent: 5, resetsIn: TimeSpan.FromHours(2)));

        var first = Assert.Single(
            new QuotaNotificationProjector(() => Now).Project(firstSnapshot, settings));
        var afterRestart = Assert.Single(
            new QuotaNotificationProjector(() => Now).Project(changedReadingInSameWindow, settings));

        Assert.Equal(first.Id, afterRestart.Id);
        Assert.InRange(first.Id.Length, 1, 64);
        Assert.NotEqual(first.Body, afterRestart.Body);

        var nextWindow = Assert.Single(
            new QuotaNotificationProjector(() => Now).Project(
                Snapshot(primary: Window(remainingPercent: 5, resetsIn: TimeSpan.FromHours(7))),
                settings));
        Assert.NotEqual(first.Id, nextWindow.Id);
    }

    [Fact]
    public void Project_UsesStableFallbackIdsWhenAResetTimestampIsUnavailable()
    {
        var settings = Settings();
        var first = Assert.Single(
            new QuotaNotificationProjector(() => Now).Project(
                Snapshot(primary: Window(remainingPercent: 10)),
                settings));
        var afterRestart = Assert.Single(
            new QuotaNotificationProjector(() => Now.AddMinutes(1)).Project(
                Snapshot(primary: Window(remainingPercent: 5)),
                settings));

        Assert.Equal(first.Id, afterRestart.Id);
    }

    [Fact]
    public void Project_UsesInjectedWindowIdentityAndObservesWhileDisabled()
    {
        var identities = new RecordingWindowIdentityProvider("durable-window-a");
        var snapshot = Snapshot(primary: Window(remainingPercent: 5));
        var disabled = Settings() with { NotificationsEnabled = false };

        Assert.Empty(new QuotaNotificationProjector(
            () => Now,
            identities).Project(snapshot, disabled));
        Assert.Single(identities.Observations);

        var first = Assert.Single(new QuotaNotificationProjector(
            () => Now,
            identities).Project(snapshot, Settings()));
        var second = Assert.Single(new QuotaNotificationProjector(
            () => Now,
            new RecordingWindowIdentityProvider("durable-window-b"))
            .Project(snapshot, Settings()));

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void Project_KeepsMonthlyAlertsForEachRuntimeIndependent()
    {
        var projector = Projector();
        var month = new TokenPeriod(
            Tokens: 1_000,
            Breakdown: TokenBreakdown.Zero,
            CreditsUsed: 1_000,
            UnratedTokens: 0,
            CreditsByModel: [],
            Quality: DataQuality.Detailed);
        var settings = Settings() with { MonthlyAmountAlert = 20 };

        Assert.Single(projector.Project(Snapshot(month: month), settings));
        Assert.Single(projector.Project(
            Snapshot(runtime: AgentRuntime.ClaudeCode, month: month),
            settings));
    }

    private static QuotaNotificationProjector Projector() => new(() => Now);

    private static AppSettings Settings() => new AppSettings(
        NotificationsEnabled: true,
        QuotaForecastAlertsEnabled: true,
        FiveHourAlertPercent: 20,
        SevenDayAlertPercent: 20,
        MonthlyAmountAlert: 0,
        MinimumRateCoverageAlertPercent: 0);

    private static DashboardSnapshot Snapshot(
        AgentRuntime runtime = AgentRuntime.Codex,
        RateLimitWindow? primary = null,
        RateLimitWindow? secondary = null,
        QuotaForecast? primaryForecast = null,
        QuotaForecast? secondaryForecast = null,
        TokenPeriod? month = null)
    {
        var snapshot = DashboardSnapshot.Empty(runtime);
        return snapshot with
        {
            RefreshedAt = Now,
            PrimaryQuota = primary,
            SecondaryQuota = secondary,
            PrimaryForecast = primaryForecast,
            SecondaryForecast = secondaryForecast,
            Tokens = snapshot.Tokens with
            {
                Month = month ?? snapshot.Tokens.Month
            }
        };
    }

    private static RateLimitWindow Window(double remainingPercent, TimeSpan? resetsIn = null) =>
        new(
            UsedPercent: 100d - remainingPercent,
            WindowDurationMinutes: 300,
            ResetsAt: resetsIn is null ? null : Now + resetsIn.Value);

    private sealed class RecordingWindowIdentityProvider(string identity)
        : IQuotaWindowIdentityProvider
    {
        public List<(AgentRuntime Runtime, string Label, double Remaining)> Observations { get; } = [];

        public string ObserveWindow(
            AgentRuntime runtime,
            string label,
            RateLimitWindow quota,
            DateTimeOffset observedAt)
        {
            Observations.Add((runtime, label, quota.RemainingPercent));
            return identity;
        }
    }
}
