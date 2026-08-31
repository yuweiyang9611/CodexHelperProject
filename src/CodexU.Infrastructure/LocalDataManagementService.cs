using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class LocalDataManagementService(
    AppSettingsStore settingsStore,
    TodoStore todoStore,
    string? applicationDataDirectory = null,
    string? electronLogDirectory = null)
{
    private const int CurrentBackupSchemaVersion = 2;
    private const long MaximumBackupBytes = 24L * 1024 * 1024;
    private const int MaximumManagedFileBytes = 8 * 1024 * 1024;
    private const string BackupHashAlgorithm = "SHA-256";
    private const string SettingsBackupPath = "settings.json";
    private const string TodosBackupPath = "todos.json";
    private const string CodexHistoryBackupPath = "history/daily-usage-codex-v1.jsonl";
    private const string ClaudeHistoryBackupPath = "history/daily-usage-claude-code-v1.jsonl";

    private static readonly string[] HistoryBackupPaths =
    [
        CodexHistoryBackupPath,
        ClaudeHistoryBackupPath
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _dataDirectory = Path.GetFullPath(applicationDataDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU"));
    private readonly string? _electronLogDirectory = ResolveOptionalDirectory(
        electronLogDirectory ?? Environment.GetEnvironmentVariable("CODEXU_ELECTRON_LOG_DIRECTORY"));
    private readonly SemaphoreSlim _dataOperationGate = new(1, 1);

    public async Task<LocalOperationResult> ExportAggregatesAsync(
        DashboardSnapshot snapshot,
        string path,
        string format,
        CancellationToken cancellationToken = default)
    {
        path = EnsureAllowedDestination(path, format.Equals("csv", StringComparison.OrdinalIgnoreCase) ? ".csv" : ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new StringBuilder("date,tokens,credits_used,quality\r\n");
                foreach (var day in snapshot.DailyUsage)
                {
                    builder.Append(day.Date.ToString("yyyy-MM-dd")).Append(',')
                        .Append(day.Tokens).Append(',')
                        .Append(day.CreditsUsed.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                        .Append(day.Quality).Append("\r\n");
                }
                await File.WriteAllTextAsync(temporaryPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            }
            else
            {
                var export = new
                {
                    schemaVersion = 1,
                    exportedAt = DateTimeOffset.Now,
                    runtime = snapshot.Runtime,
                    snapshot.RefreshedAt,
                    tokens = snapshot.Tokens,
                    dailyUsage = snapshot.DailyUsage,
                    projects = snapshot.Projects.Select(project => new
                    {
                        project.Name,
                        project.Tokens,
                        project.ThreadCount,
                        project.LastActiveAt,
                        project.Branch,
                        project.CreditsUsed,
                        project.Quality
                    }),
                    snapshot.Tools,
                    snapshot.Skills,
                    snapshot.Models,
                    privacy = "不包含对话正文、任务标题、待办、账户邮箱、完整项目路径或认证数据"
                };
                await using var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(stream, export, JsonOptions, cancellationToken);
            }

            EnsureDestinationRemainsAllowed(path);
            File.Move(temporaryPath, path, overwrite: true);
            EnsureDestinationRemainsAllowed(path);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }

        return new LocalOperationResult(true, $"聚合报表已导出：{path}", path);
    }

    public async Task<LocalOperationResult> BackupAsync(
        AppSettings settings,
        string path,
        CancellationToken cancellationToken = default)
    {
        await _dataOperationGate.WaitAsync(cancellationToken);
        try
        {
            path = EnsureAllowedDestination(path, ".json");
            var todos = await todoStore.ListAsync(cancellationToken);
            var files = new List<BackupFileEntry>
            {
                CreateBackupFile(
                    SettingsBackupPath,
                    JsonSerializer.SerializeToUtf8Bytes(settings.Normalize(), JsonOptions)),
                CreateBackupFile(
                    TodosBackupPath,
                    JsonSerializer.SerializeToUtf8Bytes(todos, JsonOptions))
            };
            foreach (var relativePath in HistoryBackupPaths)
            {
                var content = await ReadManagedFileAsync(
                    ManagedPathFor(relativePath),
                    allowMissing: true,
                    cancellationToken);
                if (content is not null)
                {
                    files.Add(CreateBackupFile(relativePath, content));
                }
            }

            var backup = new BackupDocument(
                CurrentBackupSchemaVersion,
                DateTimeOffset.Now,
                Manifest: new BackupManifest(BackupHashAlgorithm, files));
            _ = ValidateCurrentBackup(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureDestinationRemainsAllowed(path);
            var temporaryPath = CreateTemporaryPath(path);
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, backup, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                ValidateBackupFile(temporaryPath);
                EnsureDestinationRemainsAllowed(path);
                File.Move(temporaryPath, path, overwrite: true);
                EnsureDestinationRemainsAllowed(path);
                ValidateBackupFile(path);
            }
            finally
            {
                DeleteTemporaryFile(temporaryPath);
            }
            return new LocalOperationResult(true, $"设置、待办和用量历史已备份：{path}", path);
        }
        finally
        {
            _dataOperationGate.Release();
        }
    }

    public async Task<LocalDataHistorySnapshot> CaptureDailyUsageHistoryAsync(
        CancellationToken cancellationToken = default) =>
        new(await CaptureHistoryAsync(cancellationToken));

    public Task RestoreDailyUsageHistoryAsync(
        LocalDataHistorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return RestoreHistoryAsync(snapshot.Files, cancellationToken);
    }

    public async Task<LocalOperationResult> RestoreAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginRestoreAsync(path, cancellationToken);
        await transaction.CommitAsync();
        return transaction.Result;
    }

    public async Task<LocalDataRestoreTransaction> BeginRestoreAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await _dataOperationGate.WaitAsync(cancellationToken);
        var leaseTransferred = false;
        try
        {
            LocalRestoreJournal.RecoverPending(_dataDirectory);
            var (fullPath, backup) = await ReadBackupAsync(path, cancellationToken);
            var payload = backup.SchemaVersion switch
            {
                1 => ValidateLegacyBackup(backup),
                CurrentBackupSchemaVersion => ValidateCurrentBackup(backup),
                _ => throw new InvalidDataException("不支持的 codexU 备份格式。")
            };

            var previousSettings = await settingsStore.LoadAsync(cancellationToken);
            payload = payload with
            {
                Settings = payload.Settings with
                {
                    // A portable backup is data, not authorization to execute a
                    // program or alter machine startup. Keep both host-local values
                    // and require changes to go through the normal settings workflow.
                    CodexExecutable = previousSettings.CodexExecutable,
                    StartAtLogin = previousSettings.StartAtLogin
                }
            };

            var journal = await LocalRestoreJournal.PrepareAsync(
                _dataDirectory,
                payload.IncludesHistory,
                cancellationToken);
            try
            {
                var settings = await settingsStore.SaveAsync(payload.Settings, cancellationToken);
                var todos = await todoStore.ReplaceAsync(payload.Todos, cancellationToken);
                if (payload.IncludesHistory)
                {
                    await RestoreHistoryAsync(payload.History, cancellationToken);
                }

                var restoredScope = payload.IncludesHistory
                    ? "设置、待办和用量历史已恢复。"
                    : "设置和待办已恢复（旧版备份不包含用量历史）。";
                var message = restoredScope
                    + " 为安全起见，未从备份导入 Codex 可执行文件路径和本机开机启动状态。";
                var result = new LocalOperationResult(true, message, fullPath, settings, todos);
                var transaction = new LocalDataRestoreTransaction(
                    result,
                    journal,
                    () => _dataOperationGate.Release());
                leaseTransferred = true;
                return transaction;
            }
            catch (Exception exception)
            {
                try
                {
                    journal.Rollback();
                }
                catch (Exception rollbackException)
                {
                    throw new LocalDataRestoreRollbackException(
                        "恢复未完成，且事务检查点回滚失败；下次启动会再次尝试恢复。",
                        new AggregateException(exception, rollbackException));
                }

                throw;
            }
        }
        catch (LocalDataRestoreRollbackException)
        {
            throw;
        }
        catch (Exception exception) when (LocalRestoreJournal.HasPending(_dataDirectory))
        {
            throw new LocalDataRestoreRollbackException(
                "检测到未完成的恢复事务，但事务检查点无法安全回滚；下次启动会再次尝试恢复。",
                exception);
        }
        finally
        {
            if (!leaseTransferred)
            {
                _dataOperationGate.Release();
            }
        }
    }

    public async Task<LocalOperationResult> ExportDiagnosticsAsync(
        DashboardSnapshot? snapshot,
        AppSettings settings,
        string path,
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        path = EnsureAllowedDestination(path, ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entry = archive.CreateEntry("diagnostics.json", CompressionLevel.Optimal);
                await using (var entryStream = entry.Open())
                {
                    var report = new
                    {
                        schemaVersion = 1,
                        generatedAt = DateTimeOffset.Now,
                        appVersion,
                        operatingSystem = Environment.OSVersion.VersionString,
                        processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        runtime = snapshot?.Runtime,
                        snapshot?.RefreshedAt,
                        tokenQuality = snapshot?.Tokens.Lifetime.Quality,
                        tokenCount = snapshot?.Tokens.Lifetime.Tokens,
                        unratedTokens = snapshot?.Tokens.Lifetime.UnratedTokens,
                        sourceCount = snapshot?.Sources.Count,
                        projectCount = snapshot?.Projects.Count,
                        indexStatus = snapshot?.IndexStatus,
                        diagnostics = snapshot?.Diagnostics.Select(value => Sanitize(value, settings)),
                        settings = new
                        {
                            settings.Theme,
                            settings.AutoRefreshMinutes,
                            settings.IncrementalIndexEnabled,
                            settings.ShowSubagents,
                            settings.UiScalePercent,
                            customRateCount = settings.CustomModelRates?.Count ?? 0,
                            codexHomeConfigured = !string.IsNullOrWhiteSpace(settings.CodexHome),
                            codexExecutableConfigured = !string.IsNullOrWhiteSpace(settings.CodexExecutable),
                            workspaceConfigured = !string.IsNullOrWhiteSpace(settings.DefaultWorkspace)
                        },
                        privacy = "不包含对话正文、工具参数、待办内容、账户邮箱或认证数据；已对常见本地绝对路径进行脱敏"
                    };
                    await JsonSerializer.SerializeAsync(entryStream, report, JsonOptions, cancellationToken);
                }

                var startupLog = Path.Combine(_dataDirectory, "startup.log");
                if (File.Exists(startupLog))
                {
                    var logEntry = archive.CreateEntry("startup.log", CompressionLevel.Optimal);
                    await using var writer = new StreamWriter(logEntry.Open(), new UTF8Encoding(false));
                    var lines = await ReadLogTailAsync(startupLog, cancellationToken);
                    foreach (var line in lines) await writer.WriteLineAsync(Sanitize(line, settings));
                }

                if (_electronLogDirectory is not null)
                {
                    foreach (var fileName in ElectronLogFileNames())
                    {
                        var electronLog = Path.Combine(_electronLogDirectory, fileName);
                        IReadOnlyList<string> lines;
                        try
                        {
                            if (!File.Exists(electronLog)) continue;
                            lines = await ReadLogTailAsync(electronLog, cancellationToken);
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException)
                        {
                            continue;
                        }

                        var logEntry = archive.CreateEntry(
                            $"electron/{fileName}",
                            CompressionLevel.Optimal);
                        await using var writer = new StreamWriter(
                            logEntry.Open(),
                            new UTF8Encoding(false));
                        foreach (var line in lines)
                        {
                            await writer.WriteLineAsync(Sanitize(line, settings));
                        }
                    }
                }
            }

            EnsureDestinationRemainsAllowed(path);
            File.Move(temporaryPath, path, overwrite: true);
            EnsureDestinationRemainsAllowed(path);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }

        return new LocalOperationResult(true, $"脱敏诊断包已生成：{path}", path);
    }

    public Task<LocalOperationResult> RebuildSessionIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = Path.Combine(_dataDirectory, "session-index-v1.json");
        if (!File.Exists(index))
        {
            return Task.FromResult(new LocalOperationResult(true, "本地 session 索引尚未生成；下一次刷新会创建。"));
        }

        Directory.CreateDirectory(_dataDirectory);
        var backup = Path.Combine(_dataDirectory, $"session-index.rebuild-{DateTimeOffset.Now:yyyyMMddHHmmss}.json");
        File.Move(index, backup, overwrite: false);
        return Task.FromResult(new LocalOperationResult(true, "旧索引已安全备份；下一次刷新将完整重建。", backup));
    }

    private string EnsureAllowedDestination(string path, string extension)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetDirectoryName(fullPath)))
        {
            throw new ArgumentException($"导出文件必须使用 {extension} 扩展名。", nameof(path));
        }

        if (IsWithinDirectory(fullPath, _dataDirectory))
        {
            throw new InvalidOperationException("不能将导出文件写入 codexU 应用数据目录。");
        }

        var destinationDirectory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(destinationDirectory);
        EnsureDestinationRemainsAllowed(fullPath);
        return fullPath;
    }

    private void EnsureDestinationRemainsAllowed(string path)
    {
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var canonicalDataDirectory = LocalPathIdentity.CanonicalDirectoryPath(_dataDirectory);
        var canonicalDestinationDirectory = LocalPathIdentity.CanonicalDirectoryPath(destinationDirectory);
        var canonicalDestination = Path.Combine(
            canonicalDestinationDirectory,
            Path.GetFileName(path));
        if (IsWithinDirectory(canonicalDestination, canonicalDataDirectory))
        {
            throw new InvalidOperationException(
                "不能通过链接、junction 或等价路径将导出文件写入 codexU 应用数据目录。");
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(fullPath, fullDirectory, comparison)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, comparison)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, comparison));
    }

    private static string CreateTemporaryPath(string targetPath) =>
        Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

    private static string? ResolveOptionalDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> ElectronLogFileNames()
    {
        yield return "codexu.log";
        for (var index = 1; index <= 4; index++)
        {
            yield return $"codexu.{index}.log";
        }
    }

    private static BackupFileEntry CreateBackupFile(string relativePath, byte[] content) => new(
        relativePath,
        content.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(content)),
        Convert.ToBase64String(content));

    private static void ValidateBackupFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        ValidateBackupStreamLength(stream);
    }

    private static void ValidateBackupStreamLength(FileStream stream)
    {
        if (stream.Length is <= 0 or > MaximumBackupBytes)
        {
            throw new InvalidDataException("备份文件不存在、为空或超过 24 MB。");
        }
    }

    private async Task<(string FullPath, BackupDocument Document)> ReadBackupAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ValidateBackupStreamLength(stream);
        var length = checked((int)stream.Length);
        var content = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(content, cancellationToken);
            var extra = new byte[1];
            if (await stream.ReadAsync(extra, cancellationToken) != 0)
            {
                throw new InvalidDataException("备份文件在读取期间增长或超过 24 MB。");
            }

            var document = JsonSerializer.Deserialize<BackupDocument>(content, JsonOptions)
                ?? throw new InvalidDataException("备份文件内容为空。");
            return (fullPath, document);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("备份文件不是有效的 codexU JSON。", exception);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("备份文件在读取期间被截断。", exception);
        }
    }

    private RestorePayload ValidateLegacyBackup(BackupDocument backup)
    {
        if (backup.Settings is null || backup.Todos is null || backup.Manifest is not null)
        {
            throw new InvalidDataException("不支持的 codexU schema 1 备份格式。");
        }

        var settings = (backup.Settings with { CodexExecutable = null }).Validate().Normalize();
        TodoStore.ValidateReplacement(backup.Todos);
        return new RestorePayload(settings, backup.Todos, new Dictionary<string, byte[]>(), IncludesHistory: false);
    }

    private RestorePayload ValidateCurrentBackup(BackupDocument backup)
    {
        if (backup.Settings is not null
            || backup.Todos is not null
            || backup.Manifest is null
            || !string.Equals(backup.Manifest.HashAlgorithm, BackupHashAlgorithm, StringComparison.Ordinal))
        {
            throw new InvalidDataException("不支持的 codexU schema 2 备份格式。");
        }

        var allowedPaths = new HashSet<string>(
            [SettingsBackupPath, TodosBackupPath, .. HistoryBackupPaths],
            StringComparer.Ordinal);
        if (backup.Manifest.Files is null || backup.Manifest.Files.Count is < 2 or > 4)
        {
            throw new InvalidDataException("备份文件清单不完整或项目过多。");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var entry in backup.Manifest.Files)
        {
            if (entry is null
                || string.IsNullOrWhiteSpace(entry.Path)
                || !allowedPaths.Contains(entry.Path)
                || !files.TryAdd(entry.Path, []))
            {
                throw new InvalidDataException("备份文件清单包含未知、重复或无效路径。");
            }

            if (entry.Size is < 0 or > MaximumManagedFileBytes
                || entry.ContentBase64 is null
                || entry.ContentBase64.Length > ((MaximumManagedFileBytes + 2L) / 3L * 4L))
            {
                throw new InvalidDataException($"备份文件 {entry.Path} 的大小无效。");
            }

            byte[] content;
            byte[] expectedHash;
            try
            {
                content = Convert.FromBase64String(entry.ContentBase64);
                expectedHash = Convert.FromHexString(entry.Sha256 ?? string.Empty);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"备份文件 {entry.Path} 的编码或哈希无效。", exception);
            }

            if (content.LongLength != entry.Size || expectedHash.Length != SHA256.HashSizeInBytes)
            {
                throw new InvalidDataException($"备份文件 {entry.Path} 的长度或哈希无效。");
            }

            var actualHash = SHA256.HashData(content);
            if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new InvalidDataException($"备份文件 {entry.Path} 的 SHA-256 校验失败。");
            }

            totalBytes += content.LongLength;
            if (totalBytes > MaximumManagedFileBytes * 2L)
            {
                throw new InvalidDataException("备份文件清单中的数据总量过大。");
            }

            files[entry.Path] = content;
        }

        if (!files.TryGetValue(SettingsBackupPath, out var settingsBytes)
            || !files.TryGetValue(TodosBackupPath, out var todosBytes))
        {
            throw new InvalidDataException("备份文件清单缺少设置或待办。");
        }

        AppSettings settings;
        IReadOnlyList<TodoItem> todos;
        try
        {
            var importedSettings = JsonSerializer.Deserialize<AppSettings>(settingsBytes, JsonOptions)
                ?? throw new InvalidDataException("备份中的设置为空。");
            settings = (importedSettings with { CodexExecutable = null })
                .Validate()
                .Normalize();
            todos = JsonSerializer.Deserialize<IReadOnlyList<TodoItem>>(todosBytes, JsonOptions)
                ?? throw new InvalidDataException("备份中的待办为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("备份中的设置或待办 JSON 无效。", exception);
        }

        TodoStore.ValidateReplacement(todos);
        var history = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in HistoryBackupPaths)
        {
            if (files.TryGetValue(relativePath, out var content))
            {
                DailyUsageHistoryStore.ValidateBackupContent(content);
                history.Add(relativePath, content);
            }
        }

        return new RestorePayload(settings, todos, history, IncludesHistory: true);
    }

    private async Task<Dictionary<string, byte[]>> CaptureHistoryAsync(CancellationToken cancellationToken)
    {
        var history = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var relativePath in HistoryBackupPaths)
        {
            var content = await ReadManagedFileAsync(
                ManagedPathFor(relativePath),
                allowMissing: true,
                cancellationToken);
            if (content is not null)
            {
                history.Add(relativePath, content);
            }
        }

        return history;
    }

    private async Task RestoreHistoryAsync(
        IReadOnlyDictionary<string, byte[]> history,
        CancellationToken cancellationToken)
    {
        foreach (var relativePath in HistoryBackupPaths)
        {
            var targetPath = ManagedPathFor(relativePath);
            if (!history.TryGetValue(relativePath, out var content))
            {
                if (Directory.Exists(Path.GetDirectoryName(targetPath)))
                {
                    File.Delete(targetPath);
                }
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporaryPath = CreateTemporaryPath(targetPath);
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    16 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(content, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                DeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private async Task<byte[]?> ReadManagedFileAsync(
        string path,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumManagedFileBytes)
            {
                throw new InvalidDataException($"受管数据文件 {Path.GetFileName(path)} 超过 8 MB，无法安全备份或恢复。");
            }

            var content = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(content, cancellationToken);
            return content;
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return null;
        }
        catch (DirectoryNotFoundException) when (allowMissing)
        {
            return null;
        }
    }

    private string ManagedPathFor(string relativePath) => relativePath switch
    {
        SettingsBackupPath => Path.Combine(_dataDirectory, SettingsBackupPath),
        TodosBackupPath => Path.Combine(_dataDirectory, TodosBackupPath),
        CodexHistoryBackupPath => Path.Combine(_dataDirectory, "history", "daily-usage-codex-v1.jsonl"),
        ClaudeHistoryBackupPath => Path.Combine(_dataDirectory, "history", "daily-usage-claude-code-v1.jsonl"),
        _ => throw new InvalidOperationException("未知的受管备份路径。")
    };

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next export uses a unique temporary file and is not affected.
        }
    }

    private static string Sanitize(string value, AppSettings settings)
    {
        var result = value;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(profile)) result = result.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(local)) result = result.Replace(local, "%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase);
        result = ReplaceConfiguredPath(result, settings.CodexHome, "%CODEX_HOME%");
        result = ReplaceConfiguredPath(result, settings.CodexExecutable, "%CODEX_EXECUTABLE%");
        result = ReplaceConfiguredPath(result, settings.DefaultWorkspace, "%WORKSPACE%");
        result = Regex.Replace(
            result,
            @"(?i)\bfile://[^\r\n\s·；;，,\""'<>]+",
            "%LOCAL_PATH_URI%");
        result = Regex.Replace(
            result,
            @"(?i)(?<![a-z0-9])(?:[a-z]:[\\/]|\\\\)[^\r\n·；;，,\""'<>]+",
            "%LOCAL_PATH%");
        result = Regex.Replace(
            result,
            @"(?i)(?<![:/a-z0-9])/(?:home|users|tmp|var|etc|opt|usr|mnt|media|srv|run|root|private)/[^\r\n·；;，,\""'<>]+",
            "%LOCAL_PATH%");
        result = Regex.Replace(
            result,
            @"(?i)\b(authorization|proxy-authorization)\s*[:=]\s*(?:(?:bearer|basic)\s+)?[^\s,;]+",
            "$1: [REDACTED]");
        result = Regex.Replace(
            result,
            @"(?i)\b(api[-_]?key|access[-_]?token|refresh[-_]?token|client[-_]?secret|password|passwd|secret|token)\s*[:=]\s*[^\s,;&,}\]]+",
            "$1=[REDACTED]");
        result = Regex.Replace(
            result,
            @"(?i)\b(?:gh[pousr]_[a-z0-9_]{20,}|sk-[a-z0-9_-]{20,})\b",
            "[REDACTED]");
        return Regex.Replace(
            result,
            @"(?i)\b[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}\b",
            "[EMAIL]");
    }

    private static string ReplaceConfiguredPath(string value, string? path, string replacement) =>
        string.IsNullOrWhiteSpace(path)
            ? value
            : value.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<string>> ReadLogTailAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const int maximumBytes = 1024 * 1024;
        const int maximumLines = 200;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var startedMidFile = stream.Length > maximumBytes;
        if (startedMidFile)
        {
            stream.Seek(-maximumBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (startedMidFile)
        {
            _ = await reader.ReadLineAsync(cancellationToken);
        }

        var lines = new Queue<string>(maximumLines);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (lines.Count == maximumLines)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }
        return lines.ToArray();
    }

    private sealed record BackupDocument(
        int SchemaVersion,
        DateTimeOffset ExportedAt,
        AppSettings? Settings = null,
        IReadOnlyList<TodoItem>? Todos = null,
        BackupManifest? Manifest = null);

    private sealed record BackupManifest(
        string HashAlgorithm,
        IReadOnlyList<BackupFileEntry> Files);

    private sealed record BackupFileEntry(
        string Path,
        long Size,
        string Sha256,
        string ContentBase64);

    private sealed record RestorePayload(
        AppSettings Settings,
        IReadOnlyList<TodoItem> Todos,
        IReadOnlyDictionary<string, byte[]> History,
        bool IncludesHistory);

}

public sealed class LocalDataHistorySnapshot
{
    internal LocalDataHistorySnapshot(IReadOnlyDictionary<string, byte[]> files)
    {
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    internal IReadOnlyDictionary<string, byte[]> Files { get; }
}
