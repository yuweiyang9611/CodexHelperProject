using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class LocalDataManagementService(
    AppSettingsStore settingsStore,
    TodoStore todoStore,
    string? applicationDataDirectory = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _dataDirectory = Path.GetFullPath(applicationDataDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU"));

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

            File.Move(temporaryPath, path, overwrite: true);
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
        path = EnsureAllowedDestination(path, ".json");
        var todos = await todoStore.ListAsync(cancellationToken);
        var backup = new BackupDocument(1, DateTimeOffset.Now, settings.Normalize(), todos);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = CreateTemporaryPath(path);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, backup, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
        return new LocalOperationResult(true, $"设置和待办已备份：{path}", path);
    }

    public async Task<LocalOperationResult> RestoreAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists || file.Length is <= 0 or > 5 * 1024 * 1024)
        {
            throw new InvalidDataException("备份文件不存在、为空或超过 5 MB。");
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var backup = await JsonSerializer.DeserializeAsync<BackupDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("备份文件内容为空。");
        if (backup.SchemaVersion != 1 || backup.Settings is null || backup.Todos is null)
        {
            throw new InvalidDataException("不支持的 codexU 备份格式。");
        }

        var validatedSettings = backup.Settings.Validate().Normalize();
        TodoStore.ValidateReplacement(backup.Todos);
        var previousSettings = await settingsStore.LoadAsync(cancellationToken);
        var previousTodos = await todoStore.ListAsync(cancellationToken);
        try
        {
            var settings = await settingsStore.SaveAsync(validatedSettings, cancellationToken);
            var todos = await todoStore.ReplaceAsync(backup.Todos, cancellationToken);
            return new LocalOperationResult(true, "设置和待办已恢复。", fullPath, settings, todos);
        }
        catch (Exception exception)
        {
            try
            {
                await settingsStore.SaveAsync(previousSettings, CancellationToken.None);
                await todoStore.ReplaceAsync(previousTodos, CancellationToken.None);
            }
            catch (Exception rollbackException)
            {
                throw new InvalidOperationException(
                    "恢复未完成，且自动回滚失败；原始错误和回滚错误已保留。",
                    new AggregateException(exception, rollbackException));
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
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
                        privacy = "不包含对话正文、工具参数、待办内容、账户邮箱、认证数据或未脱敏路径"
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
            }

            File.Move(temporaryPath, path, overwrite: true);
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

        var reserved = new[]
        {
            "settings.json", "settings.json.bak", "todos.json", "todos.json.bak",
            "session-index-v1.json", "update-check.json", "startup.log"
        };
        if (reserved.Any(fileName => string.Equals(
                fullPath,
                Path.Combine(_dataDirectory, fileName),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("不能覆盖 codexU 正在管理的数据文件。");
        }

        return fullPath;
    }

    private static string CreateTemporaryPath(string targetPath) =>
        Path.Combine(Path.GetDirectoryName(targetPath)!, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

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
        return Regex.Replace(
            result,
            @"(?i)(?<![a-z0-9])(?:[a-z]:\\|\\\\)[^\r\n·；;，,]+",
            "%LOCAL_PATH%");
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
        AppSettings Settings,
        IReadOnlyList<TodoItem> Todos);
}
