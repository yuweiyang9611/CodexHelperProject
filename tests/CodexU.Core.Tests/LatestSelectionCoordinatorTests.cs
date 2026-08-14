using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class LatestSelectionCoordinatorTests
{
    [Fact]
    public void FailedSelection_DoesNotChangeCommittedSelection()
    {
        var coordinator = new LatestSelectionCoordinator<string>("codex");

        _ = coordinator.Begin("claude");

        Assert.Equal("codex", coordinator.Current);
    }

    [Fact]
    public void OlderSelectionCompletingLate_CannotCommit()
    {
        var coordinator = new LatestSelectionCoordinator<string>("codex");
        var older = coordinator.Begin("claude");
        var newer = coordinator.Begin("codex");

        Assert.False(coordinator.TryCommit(older));
        Assert.Equal("codex", coordinator.Current);
        Assert.True(coordinator.TryCommit(newer));
    }

    [Fact]
    public void LatestSuccessfulSelection_Wins()
    {
        var coordinator = new LatestSelectionCoordinator<string>("codex");
        var older = coordinator.Begin("claude");
        var newer = coordinator.Begin("luna");

        Assert.True(coordinator.TryCommit(newer));
        Assert.False(coordinator.TryCommit(older));
        Assert.Equal("luna", coordinator.Current);
    }
}
