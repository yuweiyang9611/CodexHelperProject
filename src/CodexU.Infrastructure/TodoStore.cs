using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class TodoStore
{
    public const int MaximumTodoCount = 10_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TodoStore(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _path = Path.Combine(directory, "todos.json");
        _backupPath = _path + ".bak";
    }

    public Task<IReadOnlyList<TodoItem>> ListAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(cancellationToken);

    public async Task<IReadOnlyList<TodoItem>> AddAsync(TodoMutation mutation, CancellationToken cancellationToken = default)
    {
        var text = mutation.Text.Trim();
        if (text.Length == 0)
        {
            return await ReadAsync(cancellationToken);
        }

        return await MutateAsync(items =>
        {
            if (items.Count >= MaximumTodoCount)
            {
                throw new InvalidOperationException($"待办不能超过 {MaximumTodoCount:N0} 项。");
            }

            items.Insert(0, new TodoItem(
                Guid.NewGuid().ToString("N"),
                text[..Math.Min(text.Length, 160)],
                false,
                NormalizePriority(mutation.Priority),
                mutation.DueDate,
                string.IsNullOrWhiteSpace(mutation.ThreadId) ? null : mutation.ThreadId,
                DateTimeOffset.Now));
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TodoItem>> UpdateAsync(TodoMutation mutation, CancellationToken cancellationToken = default) =>
        MutateAsync(items =>
        {
            var index = items.FindIndex(item => item.Id == mutation.Id);
            if (index < 0 || string.IsNullOrWhiteSpace(mutation.Text))
            {
                return;
            }

            var current = items[index];
            var text = mutation.Text.Trim();
            items[index] = current with
            {
                Text = text[..Math.Min(text.Length, 160)],
                Priority = NormalizePriority(mutation.Priority),
                DueDate = mutation.DueDate,
                UpdatedAt = DateTimeOffset.Now
            };
        }, cancellationToken);

    public Task<IReadOnlyList<TodoItem>> ToggleAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(items =>
        {
            var index = items.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                items[index] = items[index] with { Done = !items[index].Done, UpdatedAt = DateTimeOffset.Now };
            }
        }, cancellationToken);

    public Task<IReadOnlyList<TodoItem>> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        MutateAsync(items => items.RemoveAll(item => item.Id == id), cancellationToken);

    public Task<IReadOnlyList<TodoItem>> ClearCompletedAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(items => items.RemoveAll(item => item.Done), cancellationToken);

    public async Task<IReadOnlyList<TodoItem>> ReplaceAsync(
        IReadOnlyList<TodoItem> replacement,
        CancellationToken cancellationToken = default)
    {
        ValidateReplacement(replacement);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var normalized = replacement
            .Select(item =>
            {
                var id = string.IsNullOrWhiteSpace(item.Id)
                    ? Guid.NewGuid().ToString("N")
                    : item.Id[..Math.Min(128, item.Id.Length)];
                if (!ids.Add(id))
                {
                    id = Guid.NewGuid().ToString("N");
                    ids.Add(id);
                }

                var text = item.Text.Trim();
                return item with
                {
                    Id = id,
                    Text = text[..Math.Min(160, text.Length)],
                    Priority = NormalizePriority(item.Priority),
                    ThreadId = string.IsNullOrWhiteSpace(item.ThreadId) ? null : item.ThreadId[..Math.Min(256, item.ThreadId.Length)],
                    CreatedAt = item.CreatedAt == default ? DateTimeOffset.Now : item.CreatedAt
                };
            })
            .ToArray();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteCoreAsync(normalized, recoveredFromBackup: false, cancellationToken);
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TodoItem>> MutateAsync(Action<List<TodoItem>> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var readResult = await ReadCoreAsync(cancellationToken);
            var items = readResult.Items.ToList();
            mutation(items);
            await WriteCoreAsync(items, readResult.RecoveredFromBackup, cancellationToken);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TodoItem>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadCoreAsync(cancellationToken)).Items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TodoReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            if (!File.Exists(_backupPath))
            {
                return new TodoReadResult([], false);
            }

            return new TodoReadResult(await ReadFileAsync(_backupPath, cancellationToken), true);
        }

        try
        {
            return new TodoReadResult(await ReadFileAsync(_path, cancellationToken), false);
        }
        catch (Exception primaryException) when (IsRecoverableReadFailure(primaryException))
        {
            try
            {
                if (File.Exists(_backupPath))
                {
                    return new TodoReadResult(await ReadFileAsync(_backupPath, cancellationToken), true);
                }
            }
            catch (Exception backupException) when (IsRecoverableReadFailure(backupException))
            {
                throw CreateDataException(primaryException, backupException);
            }

            throw CreateDataException(primaryException, null);
        }
    }

    private static async Task<IReadOnlyList<TodoItem>> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var items = await JsonSerializer.DeserializeAsync<List<TodoItem?>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("待办文件根节点不能是 null；空待办必须使用 []。");
        if (items.Count > MaximumTodoCount)
        {
            throw new InvalidDataException($"待办文件不能超过 {MaximumTodoCount:N0} 项。");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null
                || string.IsNullOrWhiteSpace(item.Id)
                || string.IsNullOrWhiteSpace(item.Text)
                || string.IsNullOrWhiteSpace(item.Priority)
                || item.CreatedAt == default
                || !ids.Add(item.Id))
            {
                throw new InvalidDataException("待办文件包含 null、缺失必要字段或重复 ID 的无效项目。");
            }
        }

        return items.OfType<TodoItem>().ToArray();
    }

    internal static void ValidateReplacement(IReadOnlyList<TodoItem> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (replacement.Count > MaximumTodoCount)
        {
            throw new InvalidDataException($"备份中的待办不能超过 {MaximumTodoCount:N0} 项。");
        }

        if (replacement.Any(item => item is null || string.IsNullOrWhiteSpace(item.Text)))
        {
            throw new InvalidDataException("备份包含 null 或缺少正文的待办项目。");
        }
    }

    private async Task WriteCoreAsync(
        IReadOnlyList<TodoItem> items,
        bool recoveredFromBackup,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(_path))
            {
                File.Move(temporaryPath, _path);
                TryCreateInitialBackup();
                return;
            }

            if (recoveredFromBackup)
            {
                PreserveCorruptPrimary();
                File.Move(temporaryPath, _path, overwrite: true);
                return;
            }

            File.Replace(temporaryPath, _path, _backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A stale temporary file is harmless and will be replaced on the next mutation.
            }
        }
    }

    private void TryCreateInitialBackup()
    {
        try
        {
            if (!File.Exists(_backupPath))
            {
                File.Copy(_path, _backupPath, overwrite: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The primary file is already durably committed. A first-generation
            // backup is best effort; reporting failure now would make callers retry
            // an operation that has already succeeded on disk.
        }
    }

    private void PreserveCorruptPrimary()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var fileName = $"todos.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
        File.Copy(_path, Path.Combine(directory, fileName), overwrite: false);
    }

    private InvalidDataException CreateDataException(Exception primary, Exception? backup)
    {
        var message = backup is null
            ? $"待办文件无法读取，已停止写入以保护原数据：{_path}"
            : $"待办文件及其备份均无法读取，已停止写入以保护原数据：{_path}；备份：{_backupPath}";
        return new InvalidDataException(message, backup is null ? primary : new AggregateException(primary, backup));
    }

    private static bool IsRecoverableReadFailure(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static string NormalizePriority(string? priority) => priority?.ToLowerInvariant() switch
    {
        "high" => "high",
        "low" => "low",
        _ => "normal"
    };

    private sealed record TodoReadResult(IReadOnlyList<TodoItem> Items, bool RecoveredFromBackup);
}
