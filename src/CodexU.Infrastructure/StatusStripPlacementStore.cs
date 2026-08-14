using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Persists the status strip's manually selected screen position independently
/// from user-editable application settings.
/// </summary>
public sealed class StatusStripPlacementStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _placementPath;
    private readonly string _backupPath;

    public StatusStripPlacementStore(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _placementPath = Path.Combine(directory, "status-strip-placement.json");
        _backupPath = _placementPath + ".bak";
    }

    public string PlacementPath => _placementPath;

    public StatusStripPixelPoint? Load()
    {
        lock (_gate)
        {
            return TryRead(_placementPath) ?? TryRead(_backupPath);
        }
    }

    public void Save(StatusStripPixelPoint position)
    {
        if (!IsValid(position))
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "Status strip coordinates must be finite Windows screen coordinates.");
        }

        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_placementPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _placementPath + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(
                        stream,
                        new PlacementDocument(CurrentVersion, position.Left, position.Top),
                        JsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_placementPath))
                {
                    File.Replace(temporaryPath, _placementPath, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, _placementPath);
                    TryCreateInitialBackup();
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(_placementPath);
            File.Delete(_backupPath);
            File.Delete(_placementPath + ".tmp");
        }
    }

    private StatusStripPixelPoint? TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var document = JsonSerializer.Deserialize<PlacementDocument>(stream, JsonOptions);
            StatusStripPixelPoint? position = document is { Version: CurrentVersion }
                ? new StatusStripPixelPoint(document.Left, document.Top)
                : null;
            return position is { } value && IsValid(value) ? value : null;
        }
        catch (Exception exception) when (exception is JsonException
                                           or IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            // Placement is a convenience preference. A damaged or temporarily
            // unreadable file must never prevent the application from starting.
            return null;
        }
    }

    private void TryCreateInitialBackup()
    {
        try
        {
            if (!File.Exists(_backupPath))
            {
                File.Copy(_placementPath, _backupPath, overwrite: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The primary file is already durably committed; the backup is best effort.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next save recreates the temporary file.
        }
    }

    private static bool IsValid(StatusStripPixelPoint position) =>
        double.IsFinite(position.Left)
        && double.IsFinite(position.Top)
        && position.Left >= int.MinValue
        && position.Left <= int.MaxValue
        && position.Top >= int.MinValue
        && position.Top <= int.MaxValue;

    private sealed record PlacementDocument(int Version, double Left, double Top);
}
