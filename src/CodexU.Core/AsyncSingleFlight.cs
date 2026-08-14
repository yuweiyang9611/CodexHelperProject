namespace CodexU.Core;

/// <summary>
/// Shares one in-flight asynchronous operation between callers using the same key.
/// Completed, failed and cancelled operations are removed so a later call can retry.
/// </summary>
public sealed class AsyncSingleFlight<TKey, TValue>
    where TKey : notnull
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, Task<TValue>> _inFlight;

    public AsyncSingleFlight()
        : this(comparer: null)
    {
    }

    public AsyncSingleFlight(IEqualityComparer<TKey>? comparer)
    {
        _inFlight = new Dictionary<TKey, Task<TValue>>(comparer);
    }

    public Task<TValue> RunAsync(TKey key, Func<Task<TValue>> operation)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(operation);

        TaskCompletionSource<TValue> completion;
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var existing))
            {
                return existing;
            }

            completion = new TaskCompletionSource<TValue>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight.Add(key, completion.Task);
        }

        _ = ExecuteAsync(key, operation, completion);
        return completion.Task;
    }

    private async Task ExecuteAsync(
        TKey key,
        Func<Task<TValue>> operation,
        TaskCompletionSource<TValue> completion)
    {
        try
        {
            var result = await operation().ConfigureAwait(false);
            Remove(key, completion.Task);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException exception)
        {
            Remove(key, completion.Task);
            if (exception.CancellationToken.CanBeCanceled)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            else
            {
                completion.TrySetCanceled();
            }
        }
        catch (Exception exception)
        {
            Remove(key, completion.Task);
            completion.TrySetException(exception);
        }
    }

    private void Remove(TKey key, Task<TValue> expected)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var current)
                && ReferenceEquals(current, expected))
            {
                _inFlight.Remove(key);
            }
        }
    }
}
