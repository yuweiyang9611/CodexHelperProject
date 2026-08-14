using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class StatusStripPlacementStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsNegativeMultiMonitorCoordinates()
    {
        var root = CreateRoot();
        try
        {
            var store = new StatusStripPlacementStore(root);

            store.Save(new StatusStripPixelPoint(-1732, 148.5));

            Assert.Equal(new StatusStripPixelPoint(-1732, 148.5), store.Load());
            Assert.False(File.Exists(store.PlacementPath + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesLastGoodBackupWhenPrimaryIsMalformed()
    {
        var root = CreateRoot();
        try
        {
            var store = new StatusStripPlacementStore(root);
            var first = new StatusStripPixelPoint(120, 240);
            store.Save(first);
            store.Save(new StatusStripPixelPoint(360, 480));
            File.WriteAllText(store.PlacementPath, "{ malformed");

            Assert.Equal(first, store.Load());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_IgnoresUnsupportedOrUnsafeCoordinates()
    {
        var root = CreateRoot();
        try
        {
            var store = new StatusStripPlacementStore(root);
            File.WriteAllText(
                store.PlacementPath,
                """
                {
                  "version": 1,
                  "left": 2147483648,
                  "top": 20
                }
                """);

            Assert.Null(store.Load());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedSave_LeavesPreviouslyCommittedPlacementReadable()
    {
        var root = CreateRoot();
        try
        {
            var store = new StatusStripPlacementStore(root);
            var committed = new StatusStripPixelPoint(90, 180);
            store.Save(committed);
            Directory.CreateDirectory(store.PlacementPath + ".tmp");

            var exception = Record.Exception(() =>
                store.Save(new StatusStripPixelPoint(400, 500)));

            Assert.True(exception is IOException or UnauthorizedAccessException);
            Assert.Equal(committed, store.Load());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Clear_RemovesPrimaryBackupAndStaleTemporaryFile()
    {
        var root = CreateRoot();
        try
        {
            var store = new StatusStripPlacementStore(root);
            store.Save(new StatusStripPixelPoint(100, 200));
            File.WriteAllText(store.PlacementPath + ".tmp", "stale");

            store.Clear();

            Assert.Null(store.Load());
            Assert.False(File.Exists(store.PlacementPath));
            Assert.False(File.Exists(store.PlacementPath + ".bak"));
            Assert.False(File.Exists(store.PlacementPath + ".tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-status-strip-placement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
