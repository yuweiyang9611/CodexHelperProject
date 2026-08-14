using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class LocalCodexSmokeTests
{
    [Fact]
    public async Task Reader_LoadsLocalSnapshotWithoutChangingStateDatabase()
    {
        var paths = CodexPaths.Discover();
        var before = File.Exists(paths.StateDatabase) ? new FileInfo(paths.StateDatabase) : null;
        var beforeLength = before?.Length;
        var beforeWrite = before?.LastWriteTimeUtc;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var snapshot = await new CodexLocalUsageReader(paths).ReadAsync(timeout.Token);

        Assert.NotNull(snapshot);
        if (before is null)
        {
            return;
        }

        var after = new FileInfo(paths.StateDatabase);
        Assert.Equal(beforeLength, after.Length);
        Assert.Equal(beforeWrite, after.LastWriteTimeUtc);
    }
}
