using System.Collections.Concurrent;
using CodexU.Application;
using CodexU.Core;

namespace CodexU.Sidecar.Tests;

public sealed class SnapshotPublicationCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ConcurrentSharedGenerationProjectsExactlyOnce()
    {
        const int callerCount = 8;
        var coordinator = new SnapshotPublicationCoordinator();
        var snapshot = Snapshot("shared");
        var sharedGeneration = coordinator.CompleteLoad();
        var projected = 0;
        var readyCount = 0;
        var allCallersReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == callerCount)
                {
                    allCallersReady.TrySetResult();
                }

                await release.Task;
                coordinator.PublishIf(
                    sharedGeneration,
                    snapshot,
                    () => true,
                    _ => Interlocked.Increment(ref projected));
            }))
            .ToArray();

        try
        {
            await allCallersReady.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(callers).WaitAsync(TestTimeout);
        }

        Assert.Equal(1, Volatile.Read(ref projected));
        Assert.Same(snapshot, coordinator.LastSnapshot);
    }

    [Fact]
    public async Task NewerGenerationQueuesBehindActiveCallbackAndStaleContinuationIsIgnored()
    {
        var coordinator = new SnapshotPublicationCoordinator();
        var first = Snapshot("first");
        var second = Snapshot("second");
        var stale = Snapshot("stale");
        var events = new ConcurrentQueue<DashboardSnapshot>();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGeneration = coordinator.CompleteLoad();
        var secondGeneration = coordinator.CompleteLoad();

        var firstPublisher = Task.Run(() => coordinator.PublishIf(
            firstGeneration,
            first,
            () => true,
            snapshot =>
            {
                events.Enqueue(snapshot);
                firstEntered.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }));
        await firstEntered.Task.WaitAsync(TestTimeout);

        coordinator.PublishIf(secondGeneration, second, () => true, events.Enqueue);
        coordinator.PublishIf(firstGeneration, stale, () => true, events.Enqueue);

        Assert.Equal([first], events.ToArray());
        Assert.Same(second, coordinator.LastSnapshot);

        releaseFirst.TrySetResult();
        await firstPublisher.WaitAsync(TestTimeout);

        Assert.Equal([first, second], events.ToArray());
        Assert.Same(second, coordinator.LastSnapshot);
    }

    [Fact]
    public void CallbackDoesNotHoldCoordinatorLock()
    {
        var coordinator = new SnapshotPublicationCoordinator();
        var first = Snapshot("first");
        var second = Snapshot("second");
        var events = new ConcurrentQueue<DashboardSnapshot>();
        var firstGeneration = coordinator.CompleteLoad();

        coordinator.PublishIf(
            firstGeneration,
            first,
            () => true,
            snapshot =>
            {
                events.Enqueue(snapshot);
                var reentrantPublisher = Task.Run(() =>
                {
                    var secondGeneration = coordinator.CompleteLoad();
                    coordinator.PublishIf(secondGeneration, second, () => true, events.Enqueue);
                });
                Assert.True(reentrantPublisher.Wait(TestTimeout));
            });

        Assert.Equal([first, second], events.ToArray());
        Assert.Same(second, coordinator.LastSnapshot);
    }

    [Fact]
    public void SuccessfulRuntimeCommitPublishesAfterNewerCurrentLoad()
    {
        var coordinator = new SnapshotPublicationCoordinator();
        var currentRuntime = AgentRuntime.Codex;
        var selectedPreview = Snapshot("selected-preview", AgentRuntime.ClaudeCode);
        var currentRefresh = Snapshot("current-refresh", AgentRuntime.Codex);
        var selectedRefresh = Snapshot("selected-refresh", AgentRuntime.ClaudeCode);
        var oldRuntimeCompletion = Snapshot("old-runtime", AgentRuntime.Codex);
        var events = new List<DashboardSnapshot>();
        var selectedPreviewGeneration = coordinator.CompleteLoad();
        var currentRefreshGeneration = coordinator.CompleteLoad();

        coordinator.PublishIf(
            currentRefreshGeneration,
            currentRefresh,
            () => currentRuntime == AgentRuntime.Codex,
            events.Add);
        Assert.True(coordinator.TryCommitAndPublish(
            selectedPreviewGeneration,
            selectedPreview,
            () => currentRuntime == AgentRuntime.ClaudeCode,
            () =>
            {
                currentRuntime = AgentRuntime.ClaudeCode;
                return true;
            },
            events.Add));

        var oldRuntimeGeneration = coordinator.CompleteLoad();
        coordinator.PublishIf(
            oldRuntimeGeneration,
            oldRuntimeCompletion,
            () => currentRuntime == AgentRuntime.Codex,
            events.Add);
        var selectedRefreshGeneration = coordinator.CompleteLoad();
        coordinator.PublishIf(
            selectedRefreshGeneration,
            selectedRefresh,
            () => currentRuntime == AgentRuntime.ClaudeCode,
            events.Add);

        Assert.True(selectedPreviewGeneration < currentRefreshGeneration);
        Assert.Equal([currentRefresh, selectedPreview, selectedRefresh], events);
        Assert.Same(selectedRefresh, coordinator.LastSnapshot);
    }

    [Fact]
    public void SameRuntimeCommitDoesNotReplaceNewerCompletedLoad()
    {
        var coordinator = new SnapshotPublicationCoordinator();
        var currentRuntime = AgentRuntime.Codex;
        var selectedPreview = Snapshot("selected-preview");
        var newerRefresh = Snapshot("newer-refresh");
        var events = new List<DashboardSnapshot>();
        var selectedPreviewGeneration = coordinator.CompleteLoad();
        var newerRefreshGeneration = coordinator.CompleteLoad();
        coordinator.PublishIf(
            newerRefreshGeneration,
            newerRefresh,
            () => currentRuntime == AgentRuntime.Codex,
            events.Add);

        Assert.True(coordinator.TryCommitAndPublish(
            selectedPreviewGeneration,
            selectedPreview,
            () => currentRuntime == AgentRuntime.Codex,
            () => true,
            events.Add));

        Assert.Equal([newerRefresh], events);
        Assert.Same(newerRefresh, coordinator.LastSnapshot);
    }

    private static DashboardSnapshot Snapshot(
        string source,
        AgentRuntime runtime = AgentRuntime.Codex) =>
        DashboardSnapshot.Empty(runtime, source);
}
