using CodexU.Core;

namespace CodexU.Application;

/// <summary>
/// Commits loaded snapshots by generation and serializes their projection without
/// invoking application callbacks while holding the coordinator lock.
/// </summary>
internal sealed class SnapshotPublicationCoordinator
{
    private readonly object _gate = new();
    private readonly Queue<Publication> _pending = [];
    private DashboardSnapshot? _lastSnapshot;
    private long _nextGeneration;
    private long _latestGeneration = long.MinValue;
    private bool _isDraining;

    public DashboardSnapshot? LastSnapshot
    {
        get
        {
            lock (_gate)
            {
                return _lastSnapshot;
            }
        }
    }

    public long CompleteLoad()
    {
        lock (_gate)
        {
            return ++_nextGeneration;
        }
    }

    public void PublishIf(
        long generation,
        DashboardSnapshot snapshot,
        Func<bool> canPublish,
        Action<DashboardSnapshot> callback)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(canPublish);
        ArgumentNullException.ThrowIfNull(callback);

        var shouldDrain = false;
        lock (_gate)
        {
            if (generation <= _latestGeneration || !canPublish())
            {
                return;
            }

            shouldDrain = EnqueueLocked(generation, snapshot, callback);
        }

        if (shouldDrain)
        {
            Drain();
        }
    }

    public bool TryCommitAndPublish(
        long loadedGeneration,
        DashboardSnapshot snapshot,
        Func<bool> isAlreadyCurrent,
        Func<bool> tryCommit,
        Action<DashboardSnapshot> callback)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(isAlreadyCurrent);
        ArgumentNullException.ThrowIfNull(tryCommit);
        ArgumentNullException.ThrowIfNull(callback);

        var shouldDrain = false;
        lock (_gate)
        {
            var wasAlreadyCurrent = isAlreadyCurrent();
            if (!tryCommit())
            {
                return false;
            }

            if (wasAlreadyCurrent && loadedGeneration <= _latestGeneration)
            {
                return true;
            }

            var publicationGeneration = wasAlreadyCurrent
                ? loadedGeneration
                : ++_nextGeneration;
            shouldDrain = EnqueueLocked(publicationGeneration, snapshot, callback);
        }

        if (shouldDrain)
        {
            Drain();
        }

        return true;
    }

    private bool EnqueueLocked(
        long generation,
        DashboardSnapshot snapshot,
        Action<DashboardSnapshot> callback)
    {
        _latestGeneration = generation;
        _lastSnapshot = snapshot;
        _pending.Enqueue(new Publication(snapshot, callback));
        if (_isDraining)
        {
            return false;
        }

        _isDraining = true;
        return true;
    }

    private void Drain()
    {
        while (true)
        {
            Publication publication;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _isDraining = false;
                    return;
                }

                publication = _pending.Dequeue();
            }

            try
            {
                publication.Callback(publication.Snapshot);
            }
            catch
            {
                lock (_gate)
                {
                    _pending.Clear();
                    _isDraining = false;
                }

                throw;
            }
        }
    }

    private readonly record struct Publication(
        DashboardSnapshot Snapshot,
        Action<DashboardSnapshot> Callback);
}
