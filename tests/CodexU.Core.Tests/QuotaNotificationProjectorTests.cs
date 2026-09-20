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
        var snapshot = Snapshot(month: month) with
        {
            DailyUsage = [new(DateOnly.FromDateTime(Now.Date), 1_000, 1_000, DataQuality.Partial,
                Distribution: [new("gpt-5.5", "tasks", 500), new("unlisted-model", "tasks", 500)])]
        };
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
        snapshot = snapshot with { DailyUsage = [snapshot.DailyUsage[0] with { Date = DateOnly.FromDateTime(now.Date) }] };
        Assert.Equal(2, projector.Project(snapshot, settings).Count);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("retained")]
    [InlineData("legacy")]
    public void RateCoverage_UnknownHistoricalRemainderDoesNotAskForRates(string source)
    {
        var snapshot = CoverageSnapshot(source,
            [new("gpt-5.5", "tasks", 783), new("unknown", "unknown", 217)]);

        Assert.Empty(Projector().Project(snapshot, Settings() with { MinimumRateCoverageAlertPercent = 80 }));
    }

    [Fact]
    public void RateCoverage_NoModelDetailDoesNotInventMissingRates()
    {
        var snapshot = CoverageSnapshot("legacy", null);
        Assert.Empty(Projector().Project(snapshot, Settings() with { MinimumRateCoverageAlertPercent = 80 }));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("retained")]
    [InlineData("legacy")]
    public void RateCoverage_ReportsOnlyConfirmedModelsMissingApplicableRates(string source)
    {
        var snapshot = CoverageSnapshot(source,
            [new("gpt-5.5", "unknown", 400), new("private-model", "tasks", 200), new("unknown", "tasks", 400)]);
        var settings = Settings() with { MinimumRateCoverageAlertPercent = 80 };
        var projector = Projector();
        var notification = Assert.Single(projector.Project(snapshot, settings));

        Assert.StartsWith("rate-coverage:", notification.Id);
        Assert.Contains("66.7", notification.Body);
        Assert.Contains("private-model", notification.Body);
        Assert.DoesNotContain("gpt-5.5", notification.Body);
        Assert.DoesNotContain("unknown", notification.Body);
        Assert.Empty(projector.Project(snapshot, settings));
        Assert.Equal(notification.Id, Assert.Single(Projector().Project(snapshot, settings)).Id);
    }

    [Fact]
    public void RateCoverage_UsesUsageDateAndHonorsCustomAndPinnedRates()
    {
        var snapshot = CoverageSnapshot("live", [new("gpt-5.5", "tasks", 1_000)]);
        var date = DateOnly.FromDateTime(Now.Date);
        var settings = Settings() with
        {
            MinimumRateCoverageAlertPercent = 80,
            IsRateCatalogPinned = true,
            CustomModelRates = [new("gpt-5.5", 1, 1, 1, date.AddDays(1))]
        };
        Assert.Single(Projector().Project(snapshot, settings));
        Assert.Empty(Projector().Project(snapshot, settings with
        {
            CustomModelRates = [new("gpt-5.5", 1, 1, 1, date)]
        }));
        Assert.Empty(Projector().Project(snapshot, settings with { IsRateCatalogPinned = false }));
    }

    [Fact]
    public void RateCoverage_ExcludesOtherMonthsFutureDatesAndConflictingSources()
    {
        var snapshot = CoverageSnapshot("live", [new("gpt-5.5", "tasks", 1_000)]);
        var day = snapshot.DailyUsage[0] with { Distribution = [new("private-model", "tasks", 1_000)] };
        snapshot = snapshot with
        {
            DailyUsage = [snapshot.DailyUsage[0], day with { Date = day.Date.AddMonths(-1) },
                day with { Date = day.Date.AddDays(1) }, day with { Source = "conflict" }]
        };
        Assert.Empty(Projector().Project(snapshot, Settings() with { MinimumRateCoverageAlertPercent = 80 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void RateCoverage_RespectsDisabledAndSatisfiedThresholds(double threshold)
    {
        var snapshot = CoverageSnapshot("live", [new("gpt-5.5", "tasks", 500), new("private-model", "tasks", 500)]);
        Assert.Empty(Projector().Project(snapshot, Settings() with { MinimumRateCoverageAlertPercent = threshold }));
        Assert.Empty(Projector().Project(snapshot, Settings() with
        {
            MinimumRateCoverageAlertPercent = 80,
            NotificationsEnabled = false
        }));
    }

    private static DashboardSnapshot CoverageSnapshot(string source, IReadOnlyList<UsageDistributionSlice>? distribution) =>
        Snapshot(month: new(1_000, TokenBreakdown.Zero, 0, 217, [], DataQuality.Approximate)) with
        {
            DailyUsage = [new(DateOnly.FromDateTime(Now.Date), 1_000, 0, DataQuality.Approximate, source, distribution)]
        };

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
