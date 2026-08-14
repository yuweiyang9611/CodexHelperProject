namespace CodexU.Core;

public sealed class StatusStripProjection
{
    private Action<DashboardSnapshot, int?>? _sink;
    private DashboardSnapshot? _snapshot;
    private int? _openTodoCount;

    public bool HasSnapshot => _snapshot is not null;

    public void Attach(Action<DashboardSnapshot, int?> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        if (_snapshot is not null)
        {
            sink(_snapshot, _openTodoCount);
        }
    }

    public void Detach()
    {
        _sink = null;
    }

    public void PublishSnapshot(DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _sink?.Invoke(snapshot, _openTodoCount);
    }

    public void PublishOpenTodoCount(int? openTodoCount)
    {
        _openTodoCount = openTodoCount is null ? null : Math.Max(0, openTodoCount.Value);
        if (_snapshot is not null)
        {
            _sink?.Invoke(_snapshot, _openTodoCount);
        }
    }
}
