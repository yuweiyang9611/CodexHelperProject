using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Web.WebView2.Core;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfMessageBox = System.Windows.MessageBox;

namespace CodexU.App;

public partial class MainWindow
{
    private static readonly HashSet<string> MutableSettingsProperties =
    [
        "codexHome",
        "codexExecutable",
        "defaultWorkspace",
        "theme",
        "showSubagents",
        "compactMode",
        "statusStripEnabled",
        "statusStripPositionLocked",
        "desktopMode",
        "closeToTray",
        "startAtLogin",
        "notificationsEnabled",
        "quotaForecastAlertsEnabled",
        "fiveHourAlertPercent",
        "sevenDayAlertPercent",
        "autoRefreshMinutes",
        "incrementalIndexEnabled",
        "uiScalePercent",
        "amountPerThousandCredits",
        "creditCurrencySymbol",
        "codexMonthlySubscriptionAmount",
        "claudeMonthlySubscriptionAmount",
        "codexAutoDetectSubscriptionAmount",
        "claudeAutoDetectSubscriptionAmount",
        "checkForUpdates",
        "includePrereleaseUpdates",
        "monthlyAmountAlert",
        "minimumRateCoverageAlertPercent",
        "globalHotKey",
        "statusStripQuotaMode",
        "statusStripShowTodayTokens",
        "customModelRates"
    ];

    private void ConfigureWebView()
    {
        var settings = WebView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = Debugger.IsAttached;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        WebView.CoreWebView2.ProcessFailed += (_, args) =>
        {
            if (_isClosed)
            {
                return;
            }

            _webReady = false;
            LoadingOverlay.Visibility = Visibility.Visible;
            StatusText.Text = $"WebView2 进程异常：{args.ProcessFailedKind}。本机数据和顶部状态条仍会继续刷新。";
        };
        WebView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess)
            {
                StatusText.Text = $"界面加载失败：{args.WebErrorStatus}";
                return;
            }

            _webReady = true;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ApplySettings(_settings, notifyWeb: true);
        };
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IpcSecurityPolicy.IsTrustedSource(e.Source))
        {
            return;
        }

        IpcRequest? request;
        try
        {
            var messageJson = e.WebMessageAsJson;
            if (messageJson.Length > 1024 * 1024)
            {
                return;
            }

            request = JsonSerializer.Deserialize<IpcRequest>(messageJson, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (!IpcSecurityPolicy.IsValidRequest(request))
        {
            return;
        }

        IpcResponse response;
        try
        {
            var payload = await DispatchAsync(request);
            response = IpcResponse.Success(request.Id, payload);
        }
        catch (NotSupportedException ex)
        {
            response = IpcResponse.Failure(request.Id, "method_not_allowed", ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            if (_isClosed)
            {
                return;
            }

            response = IpcResponse.Failure(request.Id, "operation_cancelled", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = IpcResponse.Failure(request.Id, "host_error", ex.Message);
        }

        PostMessage(response);
    }

    private async Task<object?> DispatchAsync(IpcRequest request)
    {
        if (!IpcSecurityPolicy.IsAllowedMethod(request.Method))
        {
            throw new NotSupportedException($"不允许的 IPC 方法：{request.Method}");
        }

        switch (request.Method)
        {
            case "app.initialize":
                return new InitializeResult(
                    CurrentVersion,
                    "windows",
                    "dark",
                    false,
                    [
                        "usage",
                        "runtime",
                        "claudeCode",
                        "combinedRuntime",
                        HostCapabilityNames.Tray,
                        HostCapabilityNames.AlwaysOnTop,
                        HostCapabilityNames.GlobalHotKey,
                        HostCapabilityNames.CompactMode,
                        HostCapabilityNames.StartupRegistration,
                        HostCapabilityNames.NativeNotifications,
                        HostCapabilityNames.StatusStripControl,
                        HostCapabilityNames.DesktopMode,
                        "localOnly",
                        "updates",
                        "localData",
                        "diagnostics",
                        "rateCatalog"
                    ]);

            case "app.ready":
                SmokeTestSignals.SignalWebReady();
                return new { ready = true };

            case "usage.getSnapshot":
                return await LoadCurrentRuntimeSnapshotAsync();

            case "usage.refresh":
                var snapshot = await LoadCurrentRuntimeSnapshotAsync();
                if (snapshot.Runtime == CurrentRuntime)
                {
                    PostEvent("usage.snapshotChanged", snapshot);
                }
                return snapshot;

            case "usage.getCombined":
                return await LoadCombinedSnapshotsAsync();

            case "runtime.select":
                var runtimeName = request.Payload.TryGetProperty("runtime", out var runtimeValue)
                    ? runtimeValue.GetString()
                    : null;
                if (!Enum.TryParse<AgentRuntime>(runtimeName, ignoreCase: true, out var runtime))
                {
                    throw new ArgumentException("未知 Runtime");
                }

                var selection = _runtimeSelection.Begin(runtime);
                var runtimeSnapshot = await LoadSnapshotPreviewAsync(runtime);
                if (!_runtimeSelection.TryCommit(selection))
                {
                    throw new OperationCanceledException("运行时选择已被更新的请求取代。");
                }

                PublishSnapshotIfCurrent(runtime, runtimeSnapshot);
                PostEvent("usage.snapshotChanged", runtimeSnapshot);
                return runtimeSnapshot;

            case "settings.get":
                return _settings;

            case "settings.update":
                if (!request.Payload.TryGetProperty("patch", out var settingsPatch))
                {
                    throw new ArgumentException("设置更新缺少 patch。");
                }
                return await UpdateSettingsAsync(settings => MergeSettingsPatch(settings, settingsPatch));

            case "statusStrip.getState":
                return StatusStripStateRequested?.Invoke()
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。");

            case "statusStrip.preview":
                if (!request.Payload.TryGetProperty("patch", out var previewPatch))
                {
                    throw new ArgumentException("状态条预览缺少设置 patch。");
                }
                var previewSettings = MergeSettingsPatch(_settings, previewPatch).Validate().Normalize();
                return StatusStripPreviewRequested?.Invoke(previewSettings)
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。");

            case "statusStrip.recover":
                return StatusStripRecoverRequested?.Invoke()
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。");

            case "rates.getCatalog":
                return UsageCredits.CatalogSnapshot;

            case "rates.export":
                var rateExportDialog = new SaveFileDialog
                {
                    Title = "导出 codexU 费率目录",
                    FileName = $"codexU-rate-catalog-{DateTimeOffset.Now:yyyyMMdd}.json",
                    DefaultExt = ".json",
                    Filter = "codexU 费率目录 (*.json)|*.json",
                    AddExtension = true,
                    OverwritePrompt = true
                };
                return rateExportDialog.ShowDialog(this) == true
                    ? await _rateCatalogFileService.ExportAsync(
                        _settings.CustomModelRates,
                        rateExportDialog.FileName,
                        _lifetimeCancellation.Token,
                        completeSnapshot: _settings.IsRateCatalogPinned,
                        catalogVersion: _settings.PinnedRateCatalogVersion,
                        source: _settings.PinnedRateCatalogSource,
                        baseCatalogVersion: _settings.PinnedRateCatalogBaseVersion)
                    : new LocalOperationResult(false, "已取消导出费率目录。");

            case "rates.import":
                var rateImportDialog = new OpenFileDialog
                {
                    Title = "导入 codexU 费率目录",
                    DefaultExt = ".json",
                    Filter = "codexU 费率目录 (*.json)|*.json",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (rateImportDialog.ShowDialog(this) != true)
                {
                    return new LocalOperationResult(false, "已取消导入费率目录。");
                }

                var importedCatalog = await _rateCatalogFileService.ImportAsync(
                    rateImportDialog.FileName,
                    _lifetimeCancellation.Token);
                if (WpfMessageBox.Show(
                        this,
                        $"将用目录 {importedCatalog.CatalogVersion} 的 {importedCatalog.Rates.Count} 条费率替换当前自定义费率。是否继续？",
                        "导入费率目录",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return new LocalOperationResult(false, "已取消导入费率目录。");
                }

                var importedSettings = await UpdateSettingsAsync(settings => settings with
                {
                    CustomModelRates = importedCatalog.Rates,
                    IsRateCatalogPinned = true,
                    PinnedRateCatalogVersion = importedCatalog.CatalogVersion,
                    PinnedRateCatalogSource = importedCatalog.Source,
                    PinnedRateCatalogBaseVersion = importedCatalog.BaseCatalogVersion
                });
                var importRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return new LocalOperationResult(
                    true,
                    $"已导入费率目录 {importedCatalog.CatalogVersion}（{importedCatalog.Rates.Count} 条）。"
                    + FormatRefreshWarning(importRefreshWarning),
                    rateImportDialog.FileName,
                    importedSettings);

            case "rates.reset":
                if (WpfMessageBox.Show(
                        this,
                        "将清空所有自定义费率并恢复应用内置版本。是否继续？",
                        "恢复默认费率",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return new LocalOperationResult(false, "已取消恢复默认费率。");
                }

                var resetSettings = await UpdateSettingsAsync(settings => settings with
                {
                    CustomModelRates = [],
                    IsRateCatalogPinned = false,
                    PinnedRateCatalogVersion = null,
                    PinnedRateCatalogSource = null,
                    PinnedRateCatalogBaseVersion = null
                });
                var resetRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return new LocalOperationResult(
                    true,
                    $"已恢复内置费率目录 {UsageCredits.CurrentCatalogVersion}。"
                    + FormatRefreshWarning(resetRefreshWarning),
                    Settings: resetSettings);

            case "todos.list":
                return await InitializeTodosAsync();

            case "todos.add":
                return await MutateTodosAsync(() => _todoStore.AddAsync(ReadTodoMutation(request.Payload)));

            case "todos.update":
                return await MutateTodosAsync(() => _todoStore.UpdateAsync(ReadTodoMutation(request.Payload)));

            case "todos.toggle":
                return await MutateTodosAsync(() => _todoStore.ToggleAsync(GetRequiredString(request.Payload, "id")));

            case "todos.delete":
                return await MutateTodosAsync(() => _todoStore.DeleteAsync(GetRequiredString(request.Payload, "id")));

            case "todos.clearCompleted":
                return await MutateTodosAsync(() => _todoStore.ClearCompletedAsync());

            case "update.check":
                var forceUpdateCheck = request.Payload.TryGetProperty("force", out var forceValue)
                    && forceValue.ValueKind is JsonValueKind.True;
                _lastUpdateResult = await _updateService.CheckAsync(
                    CurrentVersion,
                    _settings.IncludePrereleaseUpdates,
                    forceUpdateCheck,
                    _lifetimeCancellation.Token);
                return _lastUpdateResult;

            case "update.openRelease":
                var releaseUrl = _lastUpdateResult?.ReleaseUrl ?? GitHubUpdateService.ReleasesPage;
                Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                return new { opened = true };

            case "data.exportAggregates":
                if (_lastSnapshot is null)
                {
                    throw new InvalidOperationException("尚未生成可导出的数据快照。");
                }
                var exportFormat = request.Payload.TryGetProperty("format", out var formatValue)
                    && string.Equals(formatValue.GetString(), "csv", StringComparison.OrdinalIgnoreCase)
                    ? "csv"
                    : "json";
                var exportDialog = new SaveFileDialog
                {
                    Title = "导出 codexU 聚合统计",
                    FileName = $"codexU-{CurrentRuntime}-{DateTimeOffset.Now:yyyyMMdd}.{exportFormat}",
                    DefaultExt = $".{exportFormat}",
                    Filter = exportFormat == "csv" ? "CSV 文件 (*.csv)|*.csv" : "JSON 文件 (*.json)|*.json",
                    AddExtension = true,
                    OverwritePrompt = true
                };
                return exportDialog.ShowDialog(this) == true
                    ? await _dataManagementService.ExportAggregatesAsync(
                        _lastSnapshot,
                        exportDialog.FileName,
                        exportFormat,
                        _lifetimeCancellation.Token)
                    : new LocalOperationResult(false, "已取消导出。");

            case "data.backup":
                var backupDialog = new SaveFileDialog
                {
                    Title = "备份 codexU 设置和待办",
                    FileName = $"codexU-backup-{DateTimeOffset.Now:yyyyMMdd}.json",
                    DefaultExt = ".json",
                    Filter = "codexU 备份 (*.json)|*.json",
                    AddExtension = true,
                    OverwritePrompt = true
                };
                return backupDialog.ShowDialog(this) == true
                    ? await BackupStateAsync(
                        backupDialog.FileName,
                        _lifetimeCancellation.Token)
                    : new LocalOperationResult(false, "已取消备份。");

            case "data.restore":
                var restoreDialog = new OpenFileDialog
                {
                    Title = "恢复 codexU 设置和待办",
                    DefaultExt = ".json",
                    Filter = "codexU 备份 (*.json)|*.json",
                    CheckFileExists = true,
                    Multiselect = false
                };
                if (restoreDialog.ShowDialog(this) != true)
                {
                    return new LocalOperationResult(false, "已取消恢复。");
                }
                if (WpfMessageBox.Show(
                        this,
                        "恢复会替换当前设置和待办。是否继续？",
                        "恢复 codexU 备份",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return new LocalOperationResult(false, "已取消恢复。");
                }
                var restoredState = await RestoreStateAsync(
                    restoreDialog.FileName,
                    _lifetimeCancellation.Token);
                var restoreRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return restoreRefreshWarning is null
                    ? restoredState
                    : restoredState with
                    {
                        Message = restoredState.Message + FormatRefreshWarning(restoreRefreshWarning)
                    };

            case "diagnostics.export":
                var diagnosticDialog = new SaveFileDialog
                {
                    Title = "生成 codexU 脱敏诊断包",
                    FileName = $"codexU-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
                    DefaultExt = ".zip",
                    Filter = "ZIP 压缩包 (*.zip)|*.zip",
                    AddExtension = true,
                    OverwritePrompt = true
                };
                return diagnosticDialog.ShowDialog(this) == true
                    ? await _dataManagementService.ExportDiagnosticsAsync(
                        _lastSnapshot,
                        _settings,
                        diagnosticDialog.FileName,
                        CurrentVersion,
                        _lifetimeCancellation.Token)
                    : new LocalOperationResult(false, "已取消生成诊断包。");

            case "diagnostics.rebuildIndex":
                return await RebuildIndexAndRefreshAsync();

            case "window.toggleCompact":
                var compactSettings = await UpdateSettingsAsync(settings => settings with
                {
                    CompactMode = !settings.CompactMode
                });
                return new { enabled = compactSettings.CompactMode };

            case "window.setAlwaysOnTop":
                Topmost = request.Payload.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean();
                return new { enabled = Topmost };

            case "window.show":
                Show();
                Activate();
                return new { visible = true };

            case "window.hide":
                Hide();
                return new { visible = false };

            default:
                throw new NotSupportedException($"不允许的 IPC 方法：{request.Method}");
        }
    }

    private async Task<string?> TryRefreshAfterCommittedChangeAsync()
    {
        try
        {
            var snapshot = await LoadCurrentRuntimeSnapshotAsync();
            if (snapshot.Runtime == CurrentRuntime)
            {
                PostEvent("usage.snapshotChanged", snapshot);
            }
            return null;
        }
        catch (Exception exception)
        {
            var message = $"用量刷新失败：{exception.Message}";
            PostEvent("usage.refreshFailed", new { message });
            return message;
        }
    }

    private async Task<LocalOperationResult> RebuildIndexAndRefreshAsync()
    {
        LocalOperationResult rebuilt;
        await _refreshGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            rebuilt = await _dataManagementService.RebuildSessionIndexAsync(_lifetimeCancellation.Token);
        }
        finally
        {
            _refreshGate.Release();
        }

        try
        {
            var snapshot = await LoadCurrentRuntimeSnapshotAsync();
            if (snapshot.Runtime == CurrentRuntime)
            {
                PostEvent("usage.snapshotChanged", snapshot);
            }
            return rebuilt;
        }
        catch (Exception exception)
        {
            var warning = $"用量刷新失败：{exception.Message}";
            PostEvent("usage.refreshFailed", new { message = warning });
            return rebuilt with { Message = rebuilt.Message + FormatRefreshWarning(warning) };
        }
    }

    private static string FormatRefreshWarning(string? warning) =>
        warning is null ? string.Empty : $" 设置已保存，但{warning}";

    private static string CurrentVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "development";

    private static TodoMutation ReadTodoMutation(JsonElement payload) =>
        JsonSerializer.Deserialize<TodoMutation>(payload.GetRawText(), JsonOptions)
        ?? throw new ArgumentException("待办数据无效");

    private static AppSettings MergeSettingsPatch(AppSettings current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("设置 patch 必须是 JSON 对象。");
        }

        var merged = JsonSerializer.SerializeToNode(current, JsonOptions)?.AsObject()
            ?? throw new InvalidOperationException("无法读取当前设置。");
        foreach (var property in patch.EnumerateObject())
        {
            if (!MutableSettingsProperties.Contains(property.Name))
            {
                throw new ArgumentException($"不允许修改设置字段：{property.Name}");
            }

            if (current.IsRateCatalogPinned
                && string.Equals(property.Name, "customModelRates", StringComparison.Ordinal))
            {
                throw new ArgumentException("锁定费率快照不能通过常规设置更新；请先恢复内置目录。");
            }

            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return merged.Deserialize<AppSettings>(JsonOptions)
            ?? throw new ArgumentException("设置 patch 无效。");
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

    private async Task<LocalOperationResult> BackupStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await _stateMutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfStateMutationUnavailable();
            return await _dataManagementService.BackupAsync(_settings, path, cancellationToken);
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    private async Task<LocalOperationResult> RestoreStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await _stateMutationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfStateMutationUnavailable();
            var previousSettings = _settings;
            var previousTodos = _lastTodos
                ?? PublishTodosAfterCommit(await _todoStore.ListAsync(cancellationToken));
            var previousDashboardService = _dashboardService;
            LocalOperationResult? restored = null;
            try
            {
                restored = await _dataManagementService.RestoreAsync(path, cancellationToken);
                var restoredSettings = restored.Settings
                    ?? throw new InvalidDataException("备份恢复结果缺少设置。");
                var restoredTodos = restored.Todos
                    ?? throw new InvalidDataException("备份恢复结果缺少待办。");
                var replacementService = DashboardSettingsChanged(previousSettings, restoredSettings)
                    ? CreateDashboardService(restoredSettings)
                    : null;

                if (previousSettings.StartAtLogin != restoredSettings.StartAtLogin)
                {
                    StartupRegistration.Apply(restoredSettings.StartAtLogin);
                }

                _settings = restoredSettings;
                if (replacementService is not null)
                {
                    _dashboardService = replacementService;
                }

                ProjectSettingsAfterCommit(_settings);
                var publishedTodos = PublishTodosAfterCommit(restoredTodos);
                return restored with { Settings = _settings, Todos = publishedTodos };
            }
            catch (Exception exception) when (restored is not null)
            {
                try
                {
                    await _settingsStore.SaveAsync(previousSettings, CancellationToken.None);
                    await _todoStore.ReplaceAsync(previousTodos, CancellationToken.None);
                    if (previousSettings.StartAtLogin != restored.Settings?.StartAtLogin)
                    {
                        StartupRegistration.Apply(previousSettings.StartAtLogin);
                    }

                    _settings = previousSettings;
                    _dashboardService = previousDashboardService;
                    ProjectSettingsAfterCommit(_settings);
                    PublishTodosAfterCommit(previousTodos);
                }
                catch (Exception rollbackException)
                {
                    throw new InvalidOperationException(
                        "恢复后的应用状态初始化失败，且自动回滚也失败。",
                        new AggregateException(exception, rollbackException));
                }

                throw;
            }
        }
        finally
        {
            _stateMutationGate.Release();
        }
    }

    private static string GetRequiredString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"缺少字段：{name}");
        }

        return value.GetString()!;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        if (IpcSecurityPolicy.IsTrustedSource(uri.AbsoluteUri)
            || string.Equals(uri.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        e.Cancel = true;
        if (uri.Scheme is "https" or "http")
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                StatusText.Text = $"无法打开外部链接：{exception.Message}";
            }
        }
    }

    private void PostEvent(string method, object? payload) => PostMessage(IpcEvent.Create(method, payload));

    private void PostMessage(object message)
    {
        if (_isClosed || (!_webReady && message is not IpcResponse))
        {
            return;
        }

        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions));
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            _webReady = false;
            if (!_isClosed)
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                StatusText.Text = "WebView2 暂不可用。本机数据和顶部状态条仍会继续刷新。";
            }
        }
    }
}
