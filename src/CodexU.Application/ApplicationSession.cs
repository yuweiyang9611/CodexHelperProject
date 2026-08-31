using System.IO;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Application;

/// <summary>
/// Owns the mutable application state and its concurrency rules independently of
/// the desktop UI framework and embedded browser implementation.
/// </summary>
public sealed class ApplicationSession : IDisposable
{
    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(90);

    private IDashboardService _dashboardService;
    private readonly AppSettingsStore _settingsStore;
    private readonly TodoStore _todoStore;
    private readonly IUpdateService _updateService;
    private readonly LocalDataManagementService _dataManagementService;
    private readonly RateCatalogFileService _rateCatalogFileService;
    private readonly IStartupRegistration _startupRegistration;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly Func<AppSettings, IDashboardService> _dashboardServiceFactory;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly AsyncSingleFlight<AgentRuntime, SnapshotLoadResult> _snapshotSingleFlight = new();
    private readonly SnapshotPublicationCoordinator _snapshotPublication = new();
    private readonly SemaphoreSlim _stateMutationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly LatestSelectionCoordinator<AgentRuntime> _runtimeSelection =
        new(AgentRuntime.Codex);

    private AppSettings _settings;
    private IReadOnlyList<TodoItem>? _lastTodos;
    private UpdateCheckResult? _lastUpdateResult;
    private volatile bool _stateShutdownRequested;
    private volatile string? _stateMutationFailure;
    private bool _disposed;

    public ApplicationSession(
        IDashboardService dashboardService,
        AppSettingsStore settingsStore,
        TodoStore todoStore,
        IUpdateService updateService,
        AppSettings settings,
        string applicationDataDirectory,
        IStartupRegistration startupRegistration,
        IHostEnvironment hostEnvironment,
        Func<AppSettings, IDashboardService>? dashboardServiceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(dashboardService);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(todoStore);
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        ArgumentNullException.ThrowIfNull(startupRegistration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        _dashboardService = dashboardService;
        _settingsStore = settingsStore;
        _todoStore = todoStore;
        _updateService = updateService;
        _settings = settings;
        var dataDirectory = Path.GetFullPath(applicationDataDirectory);
        _dataManagementService = new LocalDataManagementService(settingsStore, todoStore, dataDirectory);
        _rateCatalogFileService = new RateCatalogFileService(dataDirectory);
        _startupRegistration = startupRegistration;
        _hostEnvironment = hostEnvironment;
        _dashboardServiceFactory = dashboardServiceFactory ?? (current => DashboardService.CreateDefault(
            current.CodexHome,
            current.IncrementalIndexEnabled,
            current.DefaultWorkspace,
            current.ShowSubagents,
            current.CodexExecutable,
            current.CustomModelRates,
            current.IsRateCatalogPinned,
            applicationDataDirectory: dataDirectory));
    }

    public event Action<DashboardSnapshot>? SnapshotChanged;

    public event Action<AppSettings>? SettingsChanged;

    public event Action<IReadOnlyList<TodoItem>>? TodosChanged;

    public event Action<ProjectionWarning>? ProjectionFailed;

    public AppSettings CurrentSettings => _settings;

    public AgentRuntime CurrentRuntime => _runtimeSelection.Current;

    public DashboardSnapshot? LastSnapshot => _snapshotPublication.LastSnapshot;

    public UpdateCheckResult? LastUpdateResult => _lastUpdateResult;

    public CancellationToken LifetimeToken => _lifetimeCancellation.Token;

    public bool IsShuttingDown => _stateShutdownRequested || _lifetimeCancellation.IsCancellationRequested;

    public async Task<IReadOnlyList<TodoItem>> InitializeTodosAsync()
    {
        await _stateMutationGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ThrowIfStateMutationUnavailable();
            if (_lastTodos is not null)
            {
                return PublishTodosAfterCommit(_lastTodos);
            }

            var todos = await _todoStore.ListAsync(_lifetimeCancellation.Token);
            return PublishTodosAfterCommit(todos);
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public Task<AppSettings> SaveSettingsAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UpdateSettingsAsync(_ => settings);
    }

    public async Task<AppSettings> UpdateSettingsAsync(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _stateMutationGate.WaitAsync();
        try
        {
            ThrowIfStateMutationUnavailable();
            var previous = _settings;
            var normalized = update(previous).Validate().Normalize();
            var replacementService = DashboardSettingsChanged(previous, normalized)
                ? _dashboardServiceFactory(normalized)
                : null;

            var startupRegistrationChanged = previous.StartAtLogin != normalized.StartAtLogin;
            if (startupRegistrationChanged)
            {
                try
                {
                    await _startupRegistration.ApplyAsync(
                        normalized.StartAtLogin,
                        _lifetimeCancellation.Token);
                }
                catch (Exception applyException)
                {
                    try
                    {
                        // A timeout or read-back failure does not prove that the
                        // platform write was rejected. Compensate even when the
                        // first call failed so persisted and native state cannot
                        // silently diverge.
                        await _startupRegistration.ApplyAsync(
                            previous.StartAtLogin,
                            CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "开机启动设置失败，且原生状态自动回滚也失败。",
                            new AggregateException(applyException, rollbackException));
                    }

                    throw;
                }
            }

            AppSettings saved;
            try
            {
                saved = await _settingsStore.SaveAsync(normalized);
            }
            catch (Exception saveException)
            {
                if (startupRegistrationChanged)
                {
                    try
                    {
                        await _startupRegistration.ApplyAsync(
                            previous.StartAtLogin,
                            CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            "设置写入失败，且开机启动原生状态自动回滚也失败。",
                            new AggregateException(saveException, rollbackException));
                    }
                }

                throw;
            }

            _settings = saved;
            if (replacementService is not null)
            {
                _dashboardService = replacementService;
            }

            PublishSettingsAfterCommit(_settings);
            return _settings;
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    /// <summary>
    /// Persists an effective startup value observed by the native host without
    /// asking that same host to write it again. The expected value makes this a
    /// compare-and-swap so a newer user save wins over a stale focus refresh.
    /// </summary>
    public async Task<AppSettings> ReconcileStartupRegistrationAsync(
        bool expected,
        bool actual)
    {
        await _stateMutationGate.WaitAsync();
        try
        {
            ThrowIfStateMutationUnavailable();
            if (_settings.StartAtLogin != expected || expected == actual)
            {
                return _settings;
            }

            _settings = await _settingsStore.SaveAsync(
                _settings with { StartAtLogin = actual },
                _lifetimeCancellation.Token);
            PublishSettingsAfterCommit(_settings);
            return _settings;
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public async Task PrepareForExitAsync()
    {
        _stateShutdownRequested = true;
        await _stateMutationGate.WaitAsync();
        _stateMutationGate.Release();
    }

    public async Task<DashboardSnapshot> LoadCurrentRuntimeSnapshotAsync()
    {
        while (true)
        {
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            var requestedRuntime = CurrentRuntime;
            var snapshot = await LoadSnapshotAsync(requestedRuntime);
            if (requestedRuntime == CurrentRuntime)
            {
                return snapshot;
            }
        }
    }

    public async Task<DashboardSnapshot> SelectRuntimeAsync(AgentRuntime runtime)
    {
        var selection = _runtimeSelection.Begin(runtime);
        var loaded = await LoadSnapshotPreviewAsync(runtime);
        if (!_snapshotPublication.TryCommitAndPublish(
                loaded.Generation,
                loaded.Snapshot,
                () => runtime == CurrentRuntime,
                () => _runtimeSelection.TryCommit(selection),
                snapshot => SnapshotChanged?.Invoke(snapshot)))
        {
            throw new OperationCanceledException("运行时选择已被更新的请求取代。");
        }

        return loaded.Snapshot;
    }

    public async Task<CombinedSnapshots> LoadCombinedSnapshotsAsync()
    {
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ThrowIfStateMutationUnavailable();
            return new CombinedSnapshots(
                await ReadRuntimeForCombinedAsync(AgentRuntime.Codex),
                await ReadRuntimeForCombinedAsync(AgentRuntime.ClaudeCode));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public Task<ImportedRateCatalog> ImportRateCatalogAsync(string path) =>
        _rateCatalogFileService.ImportAsync(path, _lifetimeCancellation.Token);

    public Task<LocalOperationResult> ExportRateCatalogAsync(string path)
    {
        var settings = _settings;
        return _rateCatalogFileService.ExportAsync(
            settings.CustomModelRates,
            path,
            _lifetimeCancellation.Token,
            completeSnapshot: settings.IsRateCatalogPinned,
            catalogVersion: settings.PinnedRateCatalogVersion,
            source: settings.PinnedRateCatalogSource,
            baseCatalogVersion: settings.PinnedRateCatalogBaseVersion);
    }

    public Task<AppSettings> ApplyImportedRateCatalogAsync(ImportedRateCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return UpdateSettingsAsync(settings => settings with
        {
            CustomModelRates = catalog.Rates,
            IsRateCatalogPinned = true,
            PinnedRateCatalogVersion = catalog.CatalogVersion,
            PinnedRateCatalogSource = catalog.Source,
            PinnedRateCatalogBaseVersion = catalog.BaseCatalogVersion
        });
    }

    public Task<AppSettings> ResetRateCatalogAsync() =>
        UpdateSettingsAsync(settings => settings with
        {
            CustomModelRates = [],
            IsRateCatalogPinned = false,
            PinnedRateCatalogVersion = null,
            PinnedRateCatalogSource = null,
            PinnedRateCatalogBaseVersion = null
        });

    public Task<IReadOnlyList<TodoItem>> AddTodoAsync(TodoMutation mutation) =>
        MutateTodosAsync(() => _todoStore.AddAsync(mutation));

    public Task<IReadOnlyList<TodoItem>> UpdateTodoAsync(TodoMutation mutation) =>
        MutateTodosAsync(() => _todoStore.UpdateAsync(mutation));

    public Task<IReadOnlyList<TodoItem>> ToggleTodoAsync(string id) =>
        MutateTodosAsync(() => _todoStore.ToggleAsync(id));

    public Task<IReadOnlyList<TodoItem>> DeleteTodoAsync(string id) =>
        MutateTodosAsync(() => _todoStore.DeleteAsync(id));

    public Task<IReadOnlyList<TodoItem>> ClearCompletedTodosAsync() =>
        MutateTodosAsync(() => _todoStore.ClearCompletedAsync());

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, bool force)
    {
        _lastUpdateResult = await _updateService.CheckAsync(
            currentVersion,
            _settings.IncludePrereleaseUpdates,
            force,
            _lifetimeCancellation.Token);
        return _lastUpdateResult;
    }

    public Task<LocalOperationResult> ExportAggregatesAsync(string path, string format)
    {
        var snapshot = LastSnapshot
            ?? throw new InvalidOperationException("尚未生成可导出的数据快照。");
        return _dataManagementService.ExportAggregatesAsync(
            snapshot,
            path,
            format,
            _lifetimeCancellation.Token);
    }

    public async Task<LocalOperationResult> BackupStateAsync(string path)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        await _stateMutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfStateMutationUnavailable();
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                return await _dataManagementService.BackupAsync(
                    _settings,
                    path,
                    cancellationToken);
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public async Task<LocalOperationResult> RestoreStateAsync(string path)
    {
        var cancellationToken = _lifetimeCancellation.Token;
        await _stateMutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfStateMutationUnavailable();
            await _refreshGate.WaitAsync(cancellationToken);
            try
            {
                var previousSettings = _settings;
                var previousTodos = _lastTodos
                    ?? PublishTodosAfterCommit(await _todoStore.ListAsync(cancellationToken));
                var previousDashboardService = _dashboardService;
                LocalDataRestoreTransaction? restoreTransaction = null;
                try
                {
                    restoreTransaction = await _dataManagementService.BeginRestoreAsync(
                        path,
                        cancellationToken);
                    var restored = restoreTransaction.Result;
                    var restoredSettings = restored.Settings
                        ?? throw new InvalidDataException("备份恢复结果缺少设置。");
                    var restoredTodos = restored.Todos
                        ?? throw new InvalidDataException("备份恢复结果缺少待办。");
                    var replacementService = DashboardSettingsChanged(previousSettings, restoredSettings)
                        ? _dashboardServiceFactory(restoredSettings)
                        : null;

                    if (previousSettings.StartAtLogin != restoredSettings.StartAtLogin)
                    {
                        await _startupRegistration.ApplyAsync(
                            restoredSettings.StartAtLogin,
                            cancellationToken);
                    }

                    _settings = restoredSettings;
                    if (replacementService is not null)
                    {
                        _dashboardService = replacementService;
                    }

                    PublishSettingsAfterCommit(_settings);
                    var publishedTodos = PublishTodosAfterCommit(restoredTodos);
                    var result = restored with { Settings = _settings, Todos = publishedTodos };
                    await restoreTransaction.CommitAsync();
                    return result;
                }
                catch (LocalDataRestoreRollbackException)
                {
                    EnterFailedRestoreState();
                    throw;
                }
                catch (Exception exception) when (restoreTransaction is not null)
                {
                    var rollbackExceptions = new List<Exception>();
                    try
                    {
                        await restoreTransaction.RollbackAsync();
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackExceptions.Add(rollbackException);
                    }

                    if (previousSettings.StartAtLogin != restoreTransaction.Result.Settings?.StartAtLogin)
                    {
                        try
                        {
                            await _startupRegistration.ApplyAsync(
                                previousSettings.StartAtLogin,
                                CancellationToken.None);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackExceptions.Add(rollbackException);
                        }
                    }

                    _settings = previousSettings;
                    _dashboardService = previousDashboardService;
                    PublishSettingsAfterCommit(_settings);
                    PublishTodosAfterCommit(previousTodos);

                    if (rollbackExceptions.Count > 0)
                    {
                        EnterFailedRestoreState();
                        throw new InvalidOperationException(
                            "恢复后的应用状态初始化失败，且自动回滚也失败。",
                            new AggregateException([exception, .. rollbackExceptions]));
                    }

                    throw;
                }
            }
            finally
            {
                _refreshGate.Release();
            }
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    public Task<LocalOperationResult> ExportDiagnosticsAsync(string path, string appVersion) =>
        _dataManagementService.ExportDiagnosticsAsync(
            LastSnapshot,
            _settings,
            path,
            appVersion,
            _lifetimeCancellation.Token);

    public async Task<LocalOperationResult> RebuildSessionIndexAsync()
    {
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            return await _dataManagementService.RebuildSessionIndexAsync(_lifetimeCancellation.Token);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void CancelLifetime() => _lifetimeCancellation.Cancel();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stateShutdownRequested = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private void ThrowIfStateMutationUnavailable()
    {
        if (_stateMutationFailure is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        if (_stateShutdownRequested)
        {
            throw new InvalidOperationException("应用正在退出，不能再修改本地状态。");
        }
    }

    private void EnterFailedRestoreState() =>
        _stateMutationFailure =
            "本地状态恢复的自动回滚失败。为避免后续修改在重启恢复时丢失，已禁止继续修改；请重启 codexU。";

    private async Task<DashboardSnapshot> LoadSnapshotAsync(AgentRuntime runtime)
    {
        var loaded = await LoadSnapshotPreviewAsync(runtime);
        PublishSnapshotIfCurrent(runtime, loaded);
        return loaded.Snapshot;
    }

    private Task<SnapshotLoadResult> LoadSnapshotPreviewAsync(AgentRuntime runtime) =>
        _snapshotSingleFlight.RunAsync(runtime, () => LoadSnapshotSerializedAsync(runtime));

    private async Task<RuntimeReadResult> ReadRuntimeForCombinedAsync(AgentRuntime runtime)
    {
        try
        {
            return new RuntimeReadResult(await ReadSnapshotCoreAsync(runtime), ReadFailed: false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RuntimeReadResult(
                DashboardSnapshot.Empty(runtime, $"{runtime} 读取失败：{exception.Message}"),
                ReadFailed: true,
                exception.Message);
        }
    }

    private async Task<SnapshotLoadResult> LoadSnapshotSerializedAsync(AgentRuntime runtime)
    {
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ThrowIfStateMutationUnavailable();
            var snapshot = await ReadSnapshotCoreAsync(runtime);
            return new SnapshotLoadResult(
                _snapshotPublication.CompleteLoad(),
                snapshot);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<DashboardSnapshot> ReadSnapshotCoreAsync(AgentRuntime runtime)
    {
        using var timeout = new CancellationTokenSource(SnapshotTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            _lifetimeCancellation.Token);
        DashboardSnapshot snapshot;
        while (true)
        {
            var requestedService = _dashboardService;
            try
            {
                snapshot = await requestedService.LoadAsync(runtime, cancellation.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                     && !_lifetimeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"本机数据刷新超过 {SnapshotTimeout.TotalSeconds:0} 秒，已取消本轮读取");
            }

            if (ReferenceEquals(requestedService, _dashboardService))
            {
                break;
            }
        }

        if (!_hostEnvironment.IsGlobalHotKeyRegistered)
        {
            snapshot = snapshot with
            {
                Diagnostics = snapshot.Diagnostics
                    .Append($"全局快捷键 {_settings.GlobalHotKey} 注册失败，可能已被其他应用占用")
                    .ToArray()
            };
        }

        return snapshot;
    }

    private void PublishSnapshotIfCurrent(AgentRuntime runtime, SnapshotLoadResult loaded)
    {
        _snapshotPublication.PublishIf(
            loaded.Generation,
            loaded.Snapshot,
            () => runtime == CurrentRuntime,
            snapshot => SnapshotChanged?.Invoke(snapshot));
    }

    private readonly record struct SnapshotLoadResult(
        long Generation,
        DashboardSnapshot Snapshot);

    private void PublishSettingsAfterCommit(AppSettings settings)
    {
        var failures = InvokeSafely(SettingsChanged, settings);
        ReportProjectionFailures("设置", failures);
    }

    private IReadOnlyList<TodoItem> PublishTodosAfterCommit(IReadOnlyList<TodoItem> todos)
    {
        var published = todos.ToArray();
        _lastTodos = published;
        var failures = InvokeSafely(TodosChanged, published);
        ReportProjectionFailures("待办", failures);
        return published;
    }

    private static IReadOnlyList<Exception> InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return [];
        }

        var failures = new List<Exception>();
        foreach (var subscriber in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                subscriber(value);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private void ReportProjectionFailures(string area, IReadOnlyList<Exception> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var message = $"{area}已保存，但界面同步失败：{string.Join("；", failures.Select(item => item.Message).Distinct())}";
        try
        {
            ProjectionFailed?.Invoke(new ProjectionWarning(area, message, failures));
        }
        catch
        {
            // Projection reporting must not turn a durable mutation into a failure.
        }
    }

    private async Task<IReadOnlyList<TodoItem>> MutateTodosAsync(
        Func<Task<IReadOnlyList<TodoItem>>> mutation)
    {
        await _stateMutationGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ThrowIfStateMutationUnavailable();
            var todos = await mutation();
            return PublishTodosAfterCommit(todos);
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    private static bool DashboardSettingsChanged(AppSettings previous, AppSettings current) =>
        !string.Equals(previous.CodexHome, current.CodexHome, StringComparison.OrdinalIgnoreCase)
        || previous.IncrementalIndexEnabled != current.IncrementalIndexEnabled
        || !string.Equals(previous.DefaultWorkspace, current.DefaultWorkspace, StringComparison.OrdinalIgnoreCase)
        || previous.ShowSubagents != current.ShowSubagents
        || !string.Equals(previous.CodexExecutable, current.CodexExecutable, StringComparison.OrdinalIgnoreCase)
        || !(previous.CustomModelRates ?? []).SequenceEqual(current.CustomModelRates ?? [])
        || previous.IsRateCatalogPinned != current.IsRateCatalogPinned;
}
