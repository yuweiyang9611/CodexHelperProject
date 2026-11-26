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
    public void BackgroundCliQueryIsOptIn()
    {
        Assert.False(new AppSettings().ClaudeCliQuotaEnabled);
        Assert.True((new AppSettings() with { ClaudeCliQuotaEnabled = true }).Validate().Normalize().ClaudeCliQuotaEnabled);
    }
}
