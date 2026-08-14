using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class StatusStripProjectionTests
{
    [Fact]
    public void Attach_ReplaysSnapshotPublishedWhileStatusStripWasDisabled()
    {
        var projection = new StatusStripProjection();
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex);
        var renders = new List<(DashboardSnapshot Snapshot, int? OpenTodos)>();

        projection.PublishOpenTodoCount(3);
        projection.PublishSnapshot(snapshot);

        projection.Attach((value, openTodos) => renders.Add((value, openTodos)));

        var render = Assert.Single(renders);
        Assert.Same(snapshot, render.Snapshot);
        Assert.Equal(3, render.OpenTodos);
    }

    [Fact]
    public void SnapshotAndTodoUpdates_AreProjectedIndependently()
    {
        var projection = new StatusStripProjection();
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex);
        var renders = new List<(DashboardSnapshot Snapshot, int? OpenTodos)>();
        projection.Attach((value, openTodos) => renders.Add((value, openTodos)));

        projection.PublishSnapshot(snapshot);
        projection.PublishOpenTodoCount(4);

        Assert.Collection(
            renders,
            first =>
            {
                Assert.Same(snapshot, first.Snapshot);
                Assert.Null(first.OpenTodos);
            },
            second =>
            {
                Assert.Same(snapshot, second.Snapshot);
                Assert.Equal(4, second.OpenTodos);
            });
    }

    [Fact]
    public void Detach_RetainsLatestStateForNextStatusStrip()
    {
        var projection = new StatusStripProjection();
        var first = DashboardSnapshot.Empty(AgentRuntime.Codex);
        var second = DashboardSnapshot.Empty(AgentRuntime.ClaudeCode);
        var renders = new List<(DashboardSnapshot Snapshot, int? OpenTodos)>();
        projection.Attach((value, openTodos) => renders.Add((value, openTodos)));
        projection.PublishSnapshot(first);

        projection.Detach();
        projection.PublishOpenTodoCount(2);
        projection.PublishSnapshot(second);
        projection.Attach((value, openTodos) => renders.Add((value, openTodos)));

        Assert.Equal(2, renders.Count);
        Assert.Same(second, renders[1].Snapshot);
        Assert.Equal(2, renders[1].OpenTodos);
    }

    [Fact]
    public void Attach_ReplaysUnknownTodoCountUntilAuthoritativeReadPublishes()
    {
        var projection = new StatusStripProjection();
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex);
        var renders = new List<(DashboardSnapshot Snapshot, int? OpenTodos)>();
        projection.PublishSnapshot(snapshot);

        projection.Attach((value, openTodos) => renders.Add((value, openTodos)));

        var initial = Assert.Single(renders);
        Assert.Same(snapshot, initial.Snapshot);
        Assert.Null(initial.OpenTodos);

        projection.PublishOpenTodoCount(0);

        Assert.Equal(2, renders.Count);
        Assert.Equal(0, renders[1].OpenTodos);
    }
}
