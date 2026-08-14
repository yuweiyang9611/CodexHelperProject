namespace CodexU.Core;

public sealed class LatestSelectionCoordinator<TSelection>
{
    private readonly Lock _gate = new();
    private long _latestGeneration;
    private TSelection _current;

    public LatestSelectionCoordinator(TSelection initialSelection)
    {
        _current = initialSelection;
    }

    public TSelection Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public LatestSelectionAttempt<TSelection> Begin(TSelection selection)
    {
        lock (_gate)
        {
            _latestGeneration++;
            return new LatestSelectionAttempt<TSelection>(_latestGeneration, selection);
        }
    }

    public bool TryCommit(LatestSelectionAttempt<TSelection> attempt)
    {
        lock (_gate)
        {
            if (attempt.Generation != _latestGeneration)
            {
                return false;
            }

            _current = attempt.Selection;
            return true;
        }
    }
}

public readonly record struct LatestSelectionAttempt<TSelection>(
    long Generation,
    TSelection Selection);
