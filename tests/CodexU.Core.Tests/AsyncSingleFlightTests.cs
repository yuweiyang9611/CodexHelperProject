using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class AsyncSingleFlightTests
{
    [Fact]
    public async Task RunAsync_ConcurrentCallsWithSameKeyShareOperation()
    {
        var singleFlight = new AsyncSingleFlight<string, int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        Task<int> Operation()
        {
            Interlocked.Increment(ref invocationCount);
            return CompleteAsync();
        }

        async Task<int> CompleteAsync()
        {
            await release.Task;
            return 42;
        }

        var first = singleFlight.RunAsync("codex", Operation);
        var second = singleFlight.RunAsync("codex", Operation);

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref invocationCount));

        release.SetResult();

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
    }

    [Fact]
    public async Task RunAsync_FailedOperationCanBeRetried()
    {
        var singleFlight = new AsyncSingleFlight<string, int>();
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        async Task<int> Operation()
        {
            var attempt = Interlocked.Increment(ref invocationCount);
            if (attempt == 1)
            {
                await releaseFailure.Task;
                throw new InvalidOperationException("first attempt failed");
            }

            return 7;
        }

        var first = singleFlight.RunAsync("codex", Operation);
        var joined = singleFlight.RunAsync("codex", Operation);
        releaseFailure.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joined);

        Assert.Equal(7, await singleFlight.RunAsync("codex", Operation));
        Assert.Equal(2, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task RunAsync_DifferentKeysRunIndependently()
    {
        var singleFlight = new AsyncSingleFlight<string, string>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        async Task<string> Operation(string value)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            await release.Task;
            return value;
        }

        var codex = singleFlight.RunAsync("codex", () => Operation("codex-value"));
        var claude = singleFlight.RunAsync("claude", () => Operation("claude-value"));

        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref started));

        release.SetResult();

        Assert.Equal("codex-value", await codex);
        Assert.Equal("claude-value", await claude);
    }
}
