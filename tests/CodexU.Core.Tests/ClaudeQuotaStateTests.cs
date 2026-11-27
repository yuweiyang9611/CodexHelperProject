using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class ClaudeQuotaStateTests
{
    [Theory]
    [InlineData(ClaudeTranscriptStatus.Stale)]
    [InlineData(ClaudeTranscriptStatus.MissingResult)]
    [InlineData(ClaudeTranscriptStatus.TooLarge)]
    public void FreshCliReadingNeverAsksForAManualDesktopQuery(ClaudeTranscriptStatus transcript)
    {
        var state = new ClaudeQuotaState(ClaudeQuotaSource.CliQuery, transcript, false, ClaudeCliQuotaStatus.Fresh);
        Assert.Equal(ClaudeQuotaAction.None, state.Action);
    }

    [Fact]
    public void OtherSourcesKeepTheirDesktopQueryActions()
    {
        Assert.Equal(ClaudeQuotaAction.Query,
            new ClaudeQuotaState(ClaudeQuotaSource.DesktopHistory, ClaudeTranscriptStatus.Stale, false, ClaudeCliQuotaStatus.Unavailable).Action);
        Assert.Equal(ClaudeCliQuotaStatus.Disabled, new ClaudeQuotaState(ClaudeQuotaSource.None, ClaudeTranscriptStatus.Unbound).CliStatus);
    }

    [Fact]
    public void BackgroundCliQueryIsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(new AppSettings().ClaudeCliQuotaEnabled);
        Assert.False((new AppSettings() with { ClaudeCliQuotaEnabled = false }).Validate().Normalize().ClaudeCliQuotaEnabled);
    }

    [Fact]
    public void AntigravityQuotaReadIsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(new AppSettings().AntigravityQuotaEnabled);
        Assert.False((new AppSettings() with { AntigravityQuotaEnabled = false }).Validate().Normalize().AntigravityQuotaEnabled);
    }

    [Fact]
    public void NormalizedSettingsAlwaysCarryTheCurrentRevision()
    {
        Assert.Equal(AppSettings.CurrentSettingsRevision, new AppSettings().SettingsRevision);
        Assert.Equal(AppSettings.CurrentSettingsRevision, (new AppSettings() with { SettingsRevision = 0 }).Normalize().SettingsRevision);
    }
}
