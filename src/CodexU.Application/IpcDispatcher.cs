using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexU.Contracts;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Application;

/// <summary>
/// Implements the complete IPC method surface. Native UI actions are delegated to
/// host ports so this dispatcher can be shared by WPF, Avalonia, and tests.
/// </summary>
public sealed class IpcDispatcher : IDisposable
{
    private static readonly FrozenSet<string> MutableSettingsProperties = FrozenSet.Create(
        StringComparer.Ordinal,
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
        "customModelRates");

    private readonly ApplicationSession _session;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IIpcEventSink _eventSink;
    private readonly IUserInteractionService _userInteraction;
    private readonly IWindowCommands _windowCommands;
    private readonly IExternalUriLauncher _externalUriLauncher;
    private readonly IStatusStripCommands? _statusStripCommands;
    private bool _disposed;

    public IpcDispatcher(
        ApplicationSession session,
        IHostEnvironment hostEnvironment,
        IIpcEventSink eventSink,
        IUserInteractionService userInteraction,
        IWindowCommands windowCommands,
        IExternalUriLauncher externalUriLauncher,
        IStatusStripCommands? statusStripCommands = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(userInteraction);
        ArgumentNullException.ThrowIfNull(windowCommands);
        ArgumentNullException.ThrowIfNull(externalUriLauncher);

        _session = session;
        _hostEnvironment = hostEnvironment;
        _eventSink = eventSink;
        _userInteraction = userInteraction;
        _windowCommands = windowCommands;
        _externalUriLauncher = externalUriLauncher;
        _statusStripCommands = statusStripCommands;

        _session.SnapshotChanged += OnSnapshotChanged;
        _session.SettingsChanged += OnSettingsChanged;
        _session.ProjectionFailed += OnProjectionFailed;
    }

    public async Task<object?> DispatchAsync(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IpcSecurityPolicy.IsAllowedMethod(request.Method))
        {
            throw new NotSupportedException($"不允许的 IPC 方法：{request.Method}");
        }

        switch (request.Method)
        {
            case "app.initialize":
                return new InitializeResult(
                    _hostEnvironment.AppVersion,
                    _hostEnvironment.Platform,
                    _hostEnvironment.Theme,
                    _hostEnvironment.IsPackaged,
                    _hostEnvironment.Capabilities);

            case "app.ready":
                _hostEnvironment.SignalWebReady();
                return new { ready = true };

            case "usage.getSnapshot":
                return await _session.LoadCurrentRuntimeSnapshotAsync();

            case "usage.refresh":
                return await _session.LoadCurrentRuntimeSnapshotAsync();

            case "usage.getCombined":
                return await _session.LoadCombinedSnapshotsAsync();

            case "runtime.select":
                var runtimeName = request.Payload.TryGetProperty("runtime", out var runtimeValue)
                    ? runtimeValue.GetString()
                    : null;
                if (!Enum.TryParse<AgentRuntime>(runtimeName, ignoreCase: true, out var runtime))
                {
                    throw new ArgumentException("未知 Runtime");
                }

                return await _session.SelectRuntimeAsync(runtime);

            case "settings.get":
                return _session.CurrentSettings;

            case "settings.update":
                if (!request.Payload.TryGetProperty("patch", out var settingsPatch))
                {
                    throw new ArgumentException("设置更新缺少 patch。");
                }
                return await _session.UpdateSettingsAsync(settings => MergeSettingsPatch(settings, settingsPatch));

            case "statusStrip.getState":
                return (_statusStripCommands
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。"))
                    .GetState();

            case "statusStrip.preview":
                if (!request.Payload.TryGetProperty("patch", out var previewPatch))
                {
                    throw new ArgumentException("状态条预览缺少设置 patch。");
                }
                var previewSettings = MergeSettingsPatch(_session.CurrentSettings, previewPatch).Validate().Normalize();
                return (_statusStripCommands
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。"))
                    .Preview(previewSettings);

            case "statusStrip.recover":
                return (_statusStripCommands
                    ?? throw new InvalidOperationException("状态条控制器尚未初始化。"))
                    .Recover();

            case "rates.getCatalog":
                return UsageCredits.CatalogSnapshot;

            case "rates.export":
                var rateExportPath = await _userInteraction.PickSaveFileAsync(
                    JsonSaveRequest(
                        "导出 codexU 费率目录",
                        $"codexU-rate-catalog-{DateTimeOffset.Now:yyyyMMdd}.json",
                        "codexU 费率目录"),
                    _session.LifetimeToken);
                return rateExportPath is not null
                    ? await _session.ExportRateCatalogAsync(rateExportPath)
                    : new LocalOperationResult(false, "已取消导出费率目录。");

            case "rates.import":
                var rateImportPath = await _userInteraction.PickOpenFileAsync(
                    JsonOpenRequest("导入 codexU 费率目录", "codexU 费率目录"),
                    _session.LifetimeToken);
                if (rateImportPath is null)
                {
                    return new LocalOperationResult(false, "已取消导入费率目录。");
                }

                var importedCatalog = await _session.ImportRateCatalogAsync(rateImportPath);
                if (!await _userInteraction.ConfirmAsync(
                        new HostConfirmationRequest(
                            "导入费率目录",
                            $"将用目录 {importedCatalog.CatalogVersion} 的 {importedCatalog.Rates.Count} 条费率替换当前自定义费率。是否继续？",
                            IsWarning: true),
                        _session.LifetimeToken))
                {
                    return new LocalOperationResult(false, "已取消导入费率目录。");
                }

                var importedSettings = await _session.ApplyImportedRateCatalogAsync(importedCatalog);
                var importRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return new LocalOperationResult(
                    true,
                    $"已导入费率目录 {importedCatalog.CatalogVersion}（{importedCatalog.Rates.Count} 条）。"
                    + FormatRefreshWarning(importRefreshWarning),
                    rateImportPath,
                    importedSettings);

            case "rates.reset":
                if (!await _userInteraction.ConfirmAsync(
                        new HostConfirmationRequest(
                            "恢复默认费率",
                            "将清空所有自定义费率并恢复应用内置版本。是否继续？",
                            IsWarning: true),
                        _session.LifetimeToken))
                {
                    return new LocalOperationResult(false, "已取消恢复默认费率。");
                }

                var resetSettings = await _session.ResetRateCatalogAsync();
                var resetRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return new LocalOperationResult(
                    true,
                    $"已恢复内置费率目录 {UsageCredits.CurrentCatalogVersion}。"
                    + FormatRefreshWarning(resetRefreshWarning),
                    Settings: resetSettings);

            case "todos.list":
                return await _session.InitializeTodosAsync();

            case "todos.add":
                return await _session.AddTodoAsync(ReadTodoMutation(request.Payload));

            case "todos.update":
                return await _session.UpdateTodoAsync(ReadTodoMutation(request.Payload));

            case "todos.toggle":
                return await _session.ToggleTodoAsync(GetRequiredString(request.Payload, "id"));

            case "todos.delete":
                return await _session.DeleteTodoAsync(GetRequiredString(request.Payload, "id"));

            case "todos.clearCompleted":
                return await _session.ClearCompletedTodosAsync();

            case "update.check":
                var forceUpdateCheck = request.Payload.TryGetProperty("force", out var forceValue)
                    && forceValue.ValueKind is JsonValueKind.True;
                return await _session.CheckForUpdatesAsync(_hostEnvironment.AppVersion, forceUpdateCheck);

            case "update.openRelease":
                var releaseUrl = _session.LastUpdateResult?.ReleaseUrl ?? GitHubUpdateService.ReleasesPage;
                await _externalUriLauncher.OpenAsync(new Uri(releaseUrl, UriKind.Absolute), _session.LifetimeToken);
                return new { opened = true };

            case "data.exportAggregates":
                var exportFormat = request.Payload.TryGetProperty("format", out var formatValue)
                    && string.Equals(formatValue.GetString(), "csv", StringComparison.OrdinalIgnoreCase)
                    ? "csv"
                    : "json";
                var aggregateExportPath = await _userInteraction.PickSaveFileAsync(
                    new HostFileDialogRequest(
                        "导出 codexU 聚合统计",
                        $"codexU-{_session.CurrentRuntime}-{DateTimeOffset.Now:yyyyMMdd}.{exportFormat}",
                        $".{exportFormat}",
                        [new HostFileType(exportFormat == "csv" ? "CSV 文件" : "JSON 文件", [$"*.{exportFormat}"])],
                        OverwritePrompt: true),
                    _session.LifetimeToken);
                return aggregateExportPath is not null
                    ? await _session.ExportAggregatesAsync(aggregateExportPath, exportFormat)
                    : new LocalOperationResult(false, "已取消导出。");

            case "data.backup":
                var backupPath = await _userInteraction.PickSaveFileAsync(
                    JsonSaveRequest(
                        "备份 codexU 设置和待办",
                        $"codexU-backup-{DateTimeOffset.Now:yyyyMMdd}.json",
                        "codexU 备份"),
                    _session.LifetimeToken);
                return backupPath is not null
                    ? await _session.BackupStateAsync(backupPath)
                    : new LocalOperationResult(false, "已取消备份。");

            case "data.restore":
                var restorePath = await _userInteraction.PickOpenFileAsync(
                    JsonOpenRequest("恢复 codexU 设置和待办", "codexU 备份"),
                    _session.LifetimeToken);
                if (restorePath is null)
                {
                    return new LocalOperationResult(false, "已取消恢复。");
                }
                if (!await _userInteraction.ConfirmAsync(
                        new HostConfirmationRequest(
                            "恢复 codexU 备份",
                            "恢复会替换当前设置和待办。是否继续？",
                            IsWarning: true),
                        _session.LifetimeToken))
                {
                    return new LocalOperationResult(false, "已取消恢复。");
                }

                var restoredState = await _session.RestoreStateAsync(restorePath);
                var restoreRefreshWarning = await TryRefreshAfterCommittedChangeAsync();
                return restoreRefreshWarning is null
                    ? restoredState
                    : restoredState with
                    {
                        Message = restoredState.Message + FormatRefreshWarning(restoreRefreshWarning)
                    };

            case "diagnostics.export":
                var diagnosticPath = await _userInteraction.PickSaveFileAsync(
                    new HostFileDialogRequest(
                        "生成 codexU 脱敏诊断包",
                        $"codexU-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip",
                        ".zip",
                        [new HostFileType("ZIP 压缩包", ["*.zip"])],
                        OverwritePrompt: true),
                    _session.LifetimeToken);
                return diagnosticPath is not null
                    ? await _session.ExportDiagnosticsAsync(diagnosticPath, _hostEnvironment.AppVersion)
                    : new LocalOperationResult(false, "已取消生成诊断包。");

            case "diagnostics.rebuildIndex":
                return await RebuildIndexAndRefreshAsync();

            case "window.toggleCompact":
                var compactSettings = await _session.UpdateSettingsAsync(settings => settings with
                {
                    CompactMode = !settings.CompactMode
                });
                return new { enabled = compactSettings.CompactMode };

            case "window.setAlwaysOnTop":
                _windowCommands.SetAlwaysOnTop(
                    request.Payload.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean());
                return new { enabled = _windowCommands.IsAlwaysOnTop };

            case "window.show":
                _windowCommands.Show();
                _windowCommands.Activate();
                return new { visible = true };

            case "window.hide":
                _windowCommands.Hide();
                return new { visible = false };

            default:
                throw new NotSupportedException($"不允许的 IPC 方法：{request.Method}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.SnapshotChanged -= OnSnapshotChanged;
        _session.SettingsChanged -= OnSettingsChanged;
        _session.ProjectionFailed -= OnProjectionFailed;
    }

    private async Task<string?> TryRefreshAfterCommittedChangeAsync()
    {
        try
        {
            await _session.LoadCurrentRuntimeSnapshotAsync();
            return null;
        }
        catch (Exception exception)
        {
            var message = $"用量刷新失败：{exception.Message}";
            _eventSink.PostEvent("usage.refreshFailed", new { message });
            return message;
        }
    }

    private async Task<LocalOperationResult> RebuildIndexAndRefreshAsync()
    {
        var rebuilt = await _session.RebuildSessionIndexAsync();
        try
        {
            await _session.LoadCurrentRuntimeSnapshotAsync();
            return rebuilt;
        }
        catch (Exception exception)
        {
            var warning = $"用量刷新失败：{exception.Message}";
            _eventSink.PostEvent("usage.refreshFailed", new { message = warning });
            return rebuilt with { Message = rebuilt.Message + FormatRefreshWarning(warning) };
        }
    }

    private void OnSnapshotChanged(DashboardSnapshot snapshot) =>
        _eventSink.PostEvent("usage.snapshotChanged", snapshot);

    private void OnSettingsChanged(AppSettings settings)
    {
        _eventSink.PostEvent("settings.changed", settings);
        _eventSink.PostEvent("window.compactChanged", new { enabled = settings.CompactMode });
    }

    private void OnProjectionFailed(ProjectionWarning warning) =>
        _eventSink.PostEvent("app.projectionWarning", new { area = warning.Area, message = warning.Message });

    private static HostFileDialogRequest JsonSaveRequest(string title, string fileName, string typeName) =>
        new(
            title,
            fileName,
            ".json",
            [new HostFileType(typeName, ["*.json"])],
            OverwritePrompt: true);

    private static HostFileDialogRequest JsonOpenRequest(string title, string typeName) =>
        new(
            title,
            string.Empty,
            ".json",
            [new HostFileType(typeName, ["*.json"])],
            CheckFileExists: true);

    private static string FormatRefreshWarning(string? warning) =>
        warning is null ? string.Empty : $" 设置已保存，但{warning}";

    private static TodoMutation ReadTodoMutation(JsonElement payload) =>
        JsonSerializer.Deserialize<TodoMutation>(payload.GetRawText(), IpcJson.Options)
        ?? throw new ArgumentException("待办数据无效");

    private static AppSettings MergeSettingsPatch(AppSettings current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("设置 patch 必须是 JSON 对象。");
        }

        var merged = JsonSerializer.SerializeToNode(current, IpcJson.Options)?.AsObject()
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

        return merged.Deserialize<AppSettings>(IpcJson.Options)
            ?? throw new ArgumentException("设置 patch 无效。");
    }

    private static string GetRequiredString(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value) || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"缺少字段：{name}");
        }

        return value.GetString()!;
    }
}
