using System.Security.Cryptography;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

/// <summary>
/// Signals that a restore left an authoritative journal which could not be
/// rolled back. Callers must stop all managed-state writes and require a restart;
/// otherwise startup recovery can overwrite work performed after this failure.
/// </summary>
public sealed class LocalDataRestoreRollbackException(
    string message,
    Exception innerException) : InvalidOperationException(message, innerException);

/// <summary>
/// A restore remains provisional until the host has rebuilt its dependent state.
/// The on-disk journal is intentionally owned by Infrastructure so both the WPF
/// host and the Electron sidecar get identical crash recovery semantics.
/// </summary>
public sealed class LocalDataRestoreTransaction : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly LocalRestoreJournal _journal;
    private readonly Action _completed;
    private CompletionState _state;

    internal LocalDataRestoreTransaction(
        LocalOperationResult result,
        LocalRestoreJournal journal,
        Action completed)
    {
        Result = result;
        _journal = journal;
        _completed = completed;
    }

    public LocalOperationResult Result { get; }

    public Task CommitAsync()
    {
        lock (_sync)
        {
            if (_state != CompletionState.Pending)
            {
                return Task.CompletedTask;
            }

            _journal.Commit();
            _state = CompletionState.Committed;
            _completed();
            return Task.CompletedTask;
        }
    }

    public Task RollbackAsync()
    {
        lock (_sync)
        {
            if (_state != CompletionState.Pending)
            {
                return Task.CompletedTask;
            }

            _journal.Rollback();
            _state = CompletionState.RolledBack;
            _completed();
            return Task.CompletedTask;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RollbackAsync();
    }

    private enum CompletionState
    {
        Pending,
        Committed,
        RolledBack
    }
}

/// <summary>
/// Byte-exact checkpoint for every file a restore may mutate. The journal is
/// published only after every checkpoint file has been flushed, and it is removed
/// only after the restore is committed or the old generation has been fully put
/// back. Consequently an interrupted rollback is idempotently retried next start.
/// </summary>
internal sealed class LocalRestoreJournal
{
    internal const string JournalFileName = ".restore-transaction-v1.json";
    internal const string StagingDirectoryName = ".restore-staging-v1";

    private const string JournalTemporaryFileName = ".restore-transaction-v1.json.tmp";
    private const int CurrentSchemaVersion = 1;
    private const long MaximumCheckpointFileBytes = 24L * 1024 * 1024;
    private const long MaximumJournalBytes = 64 * 1024;

    private static readonly object RecoverySync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly RestoreTarget[] BaseTargets =
    [
        new("settings.json", "00.bin"),
        new("settings.json.bak", "01.bin"),
        new("todos.json", "02.bin"),
        new("todos.json.bak", "03.bin")
    ];

    private static readonly RestoreTarget[] HistoryTargets =
    [
        new("history/daily-usage-codex-v1.jsonl", "04.bin"),
        new("history/daily-usage-claude-code-v1.jsonl", "05.bin")
    ];

    private readonly string _dataDirectory;
    private readonly string _journalPath;
    private readonly string _stagingDirectory;
    private readonly JournalDocument _document;

    private LocalRestoreJournal(string dataDirectory, JournalDocument document)
    {
        _dataDirectory = dataDirectory;
        _journalPath = Path.Combine(dataDirectory, JournalFileName);
        _stagingDirectory = Path.Combine(dataDirectory, StagingDirectoryName);
        _document = document;
    }

    internal static void RecoverPending(string applicationDataDirectory)
    {
        var dataDirectory = Path.GetFullPath(applicationDataDirectory);
        lock (RecoverySync)
        {
            var journalPath = Path.Combine(dataDirectory, JournalFileName);
            if (!File.Exists(journalPath))
            {
                CleanupOrphans(dataDirectory);
                return;
            }

            var document = ReadAndValidateJournal(dataDirectory);
            var journal = new LocalRestoreJournal(dataDirectory, document);
            journal.RestoreCheckpoint();
            journal.RemoveJournalThenStaging();
        }
    }

    internal static bool HasPending(string applicationDataDirectory) =>
        File.Exists(Path.Combine(
            Path.GetFullPath(applicationDataDirectory),
            JournalFileName));

    internal static async Task<LocalRestoreJournal> PrepareAsync(
        string applicationDataDirectory,
        bool includesHistory,
        CancellationToken cancellationToken)
    {
        var dataDirectory = Path.GetFullPath(applicationDataDirectory);
        RecoverPending(dataDirectory);

        var journalPath = Path.Combine(dataDirectory, JournalFileName);
        var journalTemporaryPath = Path.Combine(dataDirectory, JournalTemporaryFileName);
        var stagingDirectory = Path.Combine(dataDirectory, StagingDirectoryName);
        Directory.CreateDirectory(dataDirectory);
        DeleteFile(journalTemporaryPath);
        DeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var entries = new List<JournalFileEntry>();
            foreach (var target in Targets(includesHistory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(await CaptureAsync(
                    dataDirectory,
                    stagingDirectory,
                    target,
                    cancellationToken));
            }

            var document = new JournalDocument(
                CurrentSchemaVersion,
                includesHistory,
                entries);
            await using (var stream = new FileStream(
                journalTemporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(journalTemporaryPath, journalPath, overwrite: false);
            return new LocalRestoreJournal(dataDirectory, document);
        }
        catch
        {
            // If publication succeeded the journal is authoritative and must remain
            // for startup recovery. Before publication no managed file was changed.
            if (!File.Exists(journalPath))
            {
                DeleteFile(journalTemporaryPath);
                DeleteDirectory(stagingDirectory);
            }
            throw;
        }
    }

    internal void Commit()
    {
        if (!File.Exists(_journalPath))
        {
            throw new InvalidOperationException("恢复事务 journal 在提交前已丢失。");
        }

        File.Delete(_journalPath);
        TryDeleteDirectory(_stagingDirectory);
    }

    internal void Rollback()
    {
        if (!File.Exists(_journalPath))
        {
            // Another startup instance may already have completed the idempotent
            // rollback. With no journal there is no provisional state to retain.
            return;
        }

        RestoreCheckpoint();
        RemoveJournalThenStaging();
    }

    private void RestoreCheckpoint()
    {
        var targets = Targets(_document.IncludesHistory);
        var entries = ValidateDocument(_document, targets);

        // Validate every staged byte before changing any live file. A damaged
        // checkpoint stops startup instead of turning a recoverable transaction
        // into an unrecoverable partial rollback.
        foreach (var target in targets)
        {
            var entry = entries[target.RelativePath];
            if (entry.Existed)
            {
                ValidateSnapshot(
                    Path.Combine(_stagingDirectory, target.SnapshotFileName),
                    entry);
            }
        }

        foreach (var target in targets)
        {
            var entry = entries[target.RelativePath];
            var targetPath = TargetPath(_dataDirectory, target.RelativePath);
            if (!entry.Existed)
            {
                if (Directory.Exists(Path.GetDirectoryName(targetPath)))
                {
                    File.Delete(targetPath);
                }
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporaryPath = Path.Combine(
                Path.GetDirectoryName(targetPath)!,
                $".{Path.GetFileName(targetPath)}.restore-{Guid.NewGuid():N}.tmp");
            try
            {
                CopySnapshotToTarget(
                    Path.Combine(_stagingDirectory, target.SnapshotFileName),
                    temporaryPath,
                    entry.Size);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
    }

    private void RemoveJournalThenStaging()
    {
        File.Delete(_journalPath);
        TryDeleteDirectory(_stagingDirectory);
    }

    private static async Task<JournalFileEntry> CaptureAsync(
        string dataDirectory,
        string stagingDirectory,
        RestoreTarget target,
        CancellationToken cancellationToken)
    {
        var sourcePath = TargetPath(dataDirectory, target.RelativePath);
        FileStream source;
        try
        {
            source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return new JournalFileEntry(target.RelativePath, Existed: false, 0, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new JournalFileEntry(target.RelativePath, Existed: false, 0, null);
        }

        await using (source)
        {
            var length = source.Length;
            if (length is < 0 or > MaximumCheckpointFileBytes)
            {
                throw new InvalidDataException(
                    $"恢复前状态文件 {target.RelativePath} 超过 24 MB，无法创建安全事务检查点。");
            }

            var snapshotPath = Path.Combine(stagingDirectory, target.SnapshotFileName);
            await using var destination = new FileStream(
                snapshotPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long remaining = length;
            while (remaining > 0)
            {
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException($"恢复前状态文件 {target.RelativePath} 在检查点期间被截断。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }

            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
            {
                throw new InvalidDataException($"恢复前状态文件 {target.RelativePath} 在检查点期间发生增长。");
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            return new JournalFileEntry(
                target.RelativePath,
                Existed: true,
                length,
                Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
    }

    private static JournalDocument ReadAndValidateJournal(string dataDirectory)
    {
        var journalPath = Path.Combine(dataDirectory, JournalFileName);
        using var stream = new FileStream(
            journalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumJournalBytes)
        {
            throw new InvalidDataException("恢复事务 journal 为空或超过 64 KB。");
        }

        try
        {
            var document = JsonSerializer.Deserialize<JournalDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("恢复事务 journal 内容为空。");
            _ = ValidateDocument(document, Targets(document.IncludesHistory));
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("恢复事务 journal 不是有效 JSON。", exception);
        }
    }

    private static Dictionary<string, JournalFileEntry> ValidateDocument(
        JournalDocument document,
        IReadOnlyList<RestoreTarget> targets)
    {
        if (document.SchemaVersion != CurrentSchemaVersion
            || document.Files is null
            || document.Files.Count != targets.Count)
        {
            throw new InvalidDataException("恢复事务 journal 格式不受支持或文件清单不完整。");
        }

        var entries = new Dictionary<string, JournalFileEntry>(StringComparer.Ordinal);
        foreach (var entry in document.Files)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || !entries.TryAdd(entry.Path, entry)
                || entry.Size is < 0 or > MaximumCheckpointFileBytes
                || (entry.Existed
                    ? entry.Sha256 is null || entry.Sha256.Length != SHA256.HashSizeInBytes * 2
                    : entry.Size != 0 || entry.Sha256 is not null))
            {
                throw new InvalidDataException("恢复事务 journal 包含无效或重复的文件项目。");
            }
        }

        foreach (var target in targets)
        {
            if (!entries.ContainsKey(target.RelativePath))
            {
                throw new InvalidDataException("恢复事务 journal 包含未知路径或缺少受管文件。");
            }
        }

        return entries;
    }

    private static void ValidateSnapshot(string path, JournalFileEntry entry)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != entry.Size)
        {
            throw new InvalidDataException($"恢复事务检查点 {entry.Path} 的长度校验失败。");
        }

        var actualHash = SHA256.HashData(stream);
        byte[] expectedHash;
        try
        {
            expectedHash = Convert.FromHexString(entry.Sha256!);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"恢复事务检查点 {entry.Path} 的哈希无效。", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException($"恢复事务检查点 {entry.Path} 的 SHA-256 校验失败。");
        }
    }

    private static void CopySnapshotToTarget(string snapshotPath, string targetPath, long expectedLength)
    {
        using var source = new FileStream(
            snapshotPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        using var destination = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough);
        var buffer = new byte[64 * 1024];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("恢复事务检查点在回滚期间被截断。");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }
        destination.Flush(flushToDisk: true);
    }

    private static IReadOnlyList<RestoreTarget> Targets(bool includesHistory) =>
        includesHistory ? [.. BaseTargets, .. HistoryTargets] : BaseTargets;

    private static string TargetPath(string dataDirectory, string relativePath) =>
        Path.Combine(
            dataDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static void CleanupOrphans(string dataDirectory)
    {
        TryDeleteFile(Path.Combine(dataDirectory, JournalTemporaryFileName));
        TryDeleteDirectory(Path.Combine(dataDirectory, StagingDirectoryName));
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A unique restore temp is ignored, and a stale journal temp is removed
            // on the next startup before a new transaction is prepared.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The journal has already been removed only after a full commit/rollback;
            // leftover checkpoint bytes are inert and cleaned on the next startup.
        }
    }

    private sealed record JournalDocument(
        int SchemaVersion,
        bool IncludesHistory,
        IReadOnlyList<JournalFileEntry> Files);

    private sealed record JournalFileEntry(
        string Path,
        bool Existed,
        long Size,
        string? Sha256);

    private sealed record RestoreTarget(string RelativePath, string SnapshotFileName);
}
