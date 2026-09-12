using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class LocalCodexSmokeTests
{
    [Fact]
    public async Task Reader_LoadsLocalSnapshotWithoutChangingStateDatabase()
    {
        // An actively running Codex legitimately changes its own database. This optional
        // diagnostic cannot assert immutability of another process's live files in CI.
        if (Environment.GetEnvironmentVariable("CODEXU_RUN_LIVE_DATA_SMOKE") != "1") return;
        var paths = CodexPaths.Discover();
        var before = File.Exists(paths.StateDatabase) ? new FileInfo(paths.StateDatabase) : null;
        var beforeLength = before?.Length;
        var beforeWrite = before?.LastWriteTimeUtc;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dataRoot = Path.Combine(Path.GetTempPath(), "codexu-live-smoke-" + Guid.NewGuid().ToString("N"));
        CodexU.Core.LocalUsageSnapshot snapshot;
        try { snapshot = await new CodexLocalUsageReader(paths, applicationDataDirectory: dataRoot).ReadAsync(timeout.Token); }
        finally { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, true); }

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
