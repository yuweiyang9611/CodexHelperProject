using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class StatusStripPresentationTests
{
    [Fact]
    public void UnavailableMetrics_AreNotPresentedAsZeroOrHealthy()
    {
        var presenter = new StatusStripPresenter(new AppSettings());

        var presentation = presenter.UpdateSnapshot(
            DashboardSnapshot.Empty(AgentRuntime.Codex),
            openTodoCount: null);

        Assert.Equal("--", presentation.PrimaryQuota.Text);
        Assert.Null(presentation.PrimaryQuota.ProgressPercent);
        Assert.Equal("--", presentation.SecondaryQuota.Text);
        Assert.Null(presentation.SecondaryQuota.ProgressPercent);
        Assert.Equal("--", presentation.Today.Text);
        Assert.Equal("--", presentation.SevenDays.Text);
        Assert.Equal("--", presentation.Lifetime.Text);
        Assert.Equal("--", presentation.TodoText);
        Assert.Equal(StatusStripVisualState.Unavailable, presentation.VisualState);
        Assert.Contains("不可用", presentation.StatusText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("used")]
    [InlineData("remaining")]
    public void NonFiniteUsedPercent_IsUnavailableInEveryQuotaMode(string quotaMode)
    {
        var presenter = new StatusStripPresenter(new AppSettings(StatusStripQuotaMode: quotaMode));
        var snapshot = HealthySnapshot() with
        {
            PrimaryQuota = new RateLimitWindow(double.NaN, 300, DateTimeOffset.Now.AddHours(1))
        };

        var presentation = presenter.UpdateSnapshot(snapshot, 0);

        Assert.Equal("--", presentation.PrimaryQuota.Text);
        Assert.Null(presentation.PrimaryQuota.ProgressPercent);
        Assert.Equal(StatusStripVisualState.Unavailable, presentation.VisualState);
    }

    [Fact]
    public void PartialAndApproximateTokens_HaveExplicitDegradedLabels()
    {
        var presenter = new StatusStripPresenter(new AppSettings());
        var snapshot = HealthySnapshot() with
        {
            Tokens = new TokenSummary(
                Period(1_200, DataQuality.Partial),
                Period(2_300, DataQuality.Approximate),
                Period(3_400, DataQuality.Detailed),
                Period(4_500, DataQuality.Detailed))
        };

        var presentation = presenter.UpdateSnapshot(snapshot, 0);

        Assert.Equal("部分 1.2K", presentation.Today.Text);
        Assert.Equal("约 2.3K", presentation.SevenDays.Text);
        Assert.Equal(StatusStripVisualState.Degraded, presentation.VisualState);
        Assert.Contains("降级", presentation.StatusText, StringComparison.Ordinal);
        Assert.Contains("部分", presentation.Today.AccessibleText, StringComparison.Ordinal);
        Assert.Contains("估算", presentation.SevenDays.AccessibleText, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalRawTokens_UseCorrectBillionUnit()
    {
        var presenter = new StatusStripPresenter(new AppSettings());
        var snapshot = HealthySnapshot() with
        {
            Tokens = new TokenSummary(
                Period(598_166_284, DataQuality.Detailed),
                Period(598_166_314, DataQuality.Detailed),
                Period(1_200_000_000, DataQuality.Detailed),
                Period(6_343_961_716, DataQuality.Detailed))
        };

        var presentation = presenter.UpdateSnapshot(snapshot, 0);

        Assert.Equal("598.2M", presentation.Today.Text);
        Assert.Equal("598.2M", presentation.SevenDays.Text);
        Assert.Equal("6.3B", presentation.Lifetime.Text);
        Assert.DoesNotContain("2.5B", presentation.Today.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("官方账户", presentation.Today.AccessibleText, StringComparison.Ordinal);
        Assert.Contains("本机原始统计", presentation.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshAndFailure_AreNotClearedByTodoSettingsOrSameSnapshot()
    {
        var presenter = new StatusStripPresenter(new AppSettings());
        var snapshot = HealthySnapshot();
        presenter.UpdateSnapshot(snapshot, null);

        var generation = presenter.BeginRefresh();
        Assert.Equal(StatusStripVisualState.Refreshing, presenter.Current.VisualState);

        presenter.UpdateTodoCount(4);
        presenter.ApplySettings(new AppSettings(StatusStripQuotaMode: "used"));
        presenter.UpdateSnapshot(snapshot, 4);
        Assert.Equal(StatusStripVisualState.Refreshing, presenter.Current.VisualState);

        presenter.FailRefresh(generation, "刷新失败：测试");
        presenter.UpdateTodoCount(5);
        presenter.ApplySettings(new AppSettings());
        presenter.UpdateSnapshot(snapshot, 5);

        Assert.Equal(StatusStripVisualState.Failed, presenter.Current.VisualState);
        Assert.Equal("刷新失败：测试", presenter.Current.StatusText);

        var replacement = snapshot with { RefreshedAt = snapshot.RefreshedAt.AddMinutes(1) };
        presenter.UpdateSnapshot(replacement, 5);

        Assert.Equal(StatusStripVisualState.Healthy, presenter.Current.VisualState);
    }

    [Fact]
    public void RefreshGeneration_IgnoresOlderCompletionAndAcceptsNewestSnapshot()
    {
        var presenter = new StatusStripPresenter(new AppSettings());
        var first = HealthySnapshot();
        presenter.UpdateSnapshot(first, 0);
        var olderGeneration = presenter.BeginRefresh();
        var newestGeneration = presenter.BeginRefresh();

        presenter.FailRefresh(olderGeneration, "旧刷新失败");

        Assert.Equal(StatusStripVisualState.Refreshing, presenter.Current.VisualState);

        var second = first with { RefreshedAt = first.RefreshedAt.AddMinutes(1) };
        presenter.CompleteRefresh(newestGeneration, second);

        Assert.Same(second, presenter.Snapshot);
        Assert.Equal(StatusStripVisualState.Healthy, presenter.Current.VisualState);
    }

    [Fact]
    public void SuccessfulCompletionWithoutNewSnapshot_RemainsVisibleAsFailure()
    {
        var presenter = new StatusStripPresenter(new AppSettings());
        var snapshot = HealthySnapshot();
        presenter.UpdateSnapshot(snapshot, 0);
        var generation = presenter.BeginRefresh();

        presenter.CompleteRefresh(generation, snapshot);

        Assert.Equal(StatusStripVisualState.Failed, presenter.Current.VisualState);
        Assert.Contains("未返回新快照", presenter.Current.StatusText, StringComparison.Ordinal);
    }

    private static DashboardSnapshot HealthySnapshot()
    {
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex);
        return snapshot with
        {
            RefreshedAt = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero),
            PrimaryQuota = new RateLimitWindow(25d, 300, DateTimeOffset.Now.AddHours(1)),
            SecondaryQuota = new RateLimitWindow(40d, 10_080, DateTimeOffset.Now.AddDays(1)),
            Tokens = new TokenSummary(
                Period(100, DataQuality.Detailed),
                Period(700, DataQuality.Detailed),
                Period(3_000, DataQuality.Detailed),
                Period(12_000, DataQuality.Detailed))
        };
    }

    private static TokenPeriod Period(long tokens, DataQuality quality) => new(
        tokens,
        TokenBreakdown.Zero,
        0d,
        0,
        [],
        quality);
}
