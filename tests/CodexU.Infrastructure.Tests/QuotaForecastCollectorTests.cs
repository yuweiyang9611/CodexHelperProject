using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class QuotaForecastCollectorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"codexu-forecast-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordAsync_SaysNothingOnTheFirstReading()
    {
        var collector = NewCollector();

        var (primary, secondary) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now),
            Statusline(4, Now),
            Now);

        Assert.Null(primary);
        Assert.Null(secondary);
    }

    [Fact]
    public async Task RecordAsync_ForecastsOnceEnoughTimeHasBeenMeasured()
    {
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now.AddMinutes(-20)),
            QuotaReading.None,
            Now.AddMinutes(-20));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, Now, resetsIn: TimeSpan.FromHours(4)),
            QuotaReading.None,
            Now);

        Assert.NotNull(primary);
        Assert.Equal(1d, primary.PercentPerMinute, precision: 6);
        // 70% left at 1%/min.
        Assert.Equal(70, primary.TimeToExhaustion.TotalMinutes, precision: 6);
        Assert.True(primary.ExhaustsBeforeReset);
    }

    [Fact]
    public async Task RecordAsync_DatesAReadingByWhenItWasMeasuredNotWhenItWasPickedUp()
    {
        // The statusline snapshot is written by Claude Code on its own schedule; this
        // app reads whatever is on disk at refresh time. Dating a reading by the
        // refresh would divide by the wrong elapsed time and misstate the rate.
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, measuredAt: Now.AddMinutes(-20)),
            QuotaReading.None,
            Now.AddMinutes(-19));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, measuredAt: Now),
            QuotaReading.None,
            Now);

        Assert.NotNull(primary);
        Assert.Equal(TimeSpan.FromMinutes(20), primary.MeasuredOver);
        Assert.Equal(1d, primary.PercentPerMinute, precision: 6);
    }

    [Fact]
    public async Task RecordAsync_FallsBackToTheRefreshTimeWhenAReadingIsDatedInTheFuture()
    {
        // Clock skew between the writer and this process. Left alone the reading would
        // sit past the lookback's upper bound, be filtered out, and stall the trail.
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, measuredAt: null),
            QuotaReading.None,
            Now.AddMinutes(-20));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, measuredAt: Now.AddHours(1)),
            QuotaReading.None,
            Now);

        Assert.NotNull(primary);
        Assert.Equal(TimeSpan.FromMinutes(20), primary.MeasuredOver);
    }

    [Fact]
    public async Task RecordAsync_KeepsTheTrailAcrossRestarts()
    {
        // The trail has to outlive the process: a desktop app that is closed and
        // reopened would otherwise never accumulate enough history to forecast.
        await NewCollector().RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now.AddMinutes(-20)),
            QuotaReading.None,
            Now.AddMinutes(-20));

        var (primary, _) = await NewCollector().RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, Now),
            QuotaReading.None,
            Now);

        Assert.NotNull(primary);
        Assert.Equal(TimeSpan.FromMinutes(20), primary.MeasuredOver);
    }

    [Fact]
    public async Task RecordAsync_DoesNotMeasureARateAcrossASourceSwitch()
    {
        // Codex falls back from the app server to local rollouts. The two report the
        // same window but are separate measurements; splicing them would invent a
        // jump or a stall that never happened.
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.Codex,
            new QuotaReading(Window(10, Now.AddMinutes(-20)), QuotaForecastCollector.AppServerSource),
            QuotaReading.None,
            Now.AddMinutes(-20));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.Codex,
            new QuotaReading(Window(30, Now), QuotaForecastCollector.LocalSource),
            QuotaReading.None,
            Now);

        Assert.Null(primary);
    }

    [Fact]
    public async Task RecordAsync_KeepsTheTrailWhenASourceBlinksOutForOneRefresh()
    {
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now.AddMinutes(-20)),
            QuotaReading.None,
            Now.AddMinutes(-20));
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            QuotaReading.None,
            QuotaReading.None,
            Now.AddMinutes(-10));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, Now),
            QuotaReading.None,
            Now);

        Assert.NotNull(primary);
        Assert.Equal(TimeSpan.FromMinutes(20), primary.MeasuredOver);
    }

    [Fact]
    public async Task RecordAsync_TracksTheTwoWindowsIndependently()
    {
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now.AddMinutes(-20)),
            Statusline(2, Now.AddMinutes(-20)),
            Now.AddMinutes(-20));

        var (primary, secondary) = await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(30, Now, resetsIn: TimeSpan.FromHours(4)),
            Statusline(4, Now, resetsIn: TimeSpan.FromDays(5)),
            Now);

        Assert.NotNull(primary);
        Assert.NotNull(secondary);
        Assert.Equal(1d, primary.PercentPerMinute, precision: 6);
        Assert.Equal(0.1d, secondary.PercentPerMinute, precision: 6);
    }

    [Fact]
    public async Task RecordAsync_DoesNotMixTheSameWindowAcrossRuntimes()
    {
        var collector = NewCollector();
        await collector.RecordAsync(
            AgentRuntime.ClaudeCode,
            Statusline(10, Now.AddMinutes(-20)),
            QuotaReading.None,
            Now.AddMinutes(-20));

        var (primary, _) = await collector.RecordAsync(
            AgentRuntime.Codex,
            new QuotaReading(Window(90, Now), QuotaForecastCollector.StatuslineSource),
            QuotaReading.None,
            Now);

        Assert.Null(primary);
    }

    private QuotaForecastCollector NewCollector() => new(new QuotaSampleStore(_root));

    private static QuotaReading Statusline(
        double usedPercent,
        DateTimeOffset? measuredAt,
        TimeSpan? resetsIn = null) =>
        new(Window(usedPercent, measuredAt, resetsIn), QuotaForecastCollector.StatuslineSource);

    private static RateLimitWindow Window(
        double usedPercent,
        DateTimeOffset? measuredAt,
        TimeSpan? resetsIn = null) =>
        new(usedPercent, 300, resetsIn is null ? null : Now + resetsIn.Value, measuredAt);
}
