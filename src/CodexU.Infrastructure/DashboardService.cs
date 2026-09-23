using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class DashboardService(
    IAppServerClient appServerClient,
    ILocalUsageReader localUsageReader,
    ILocalUsageReader? claudeUsageReader = null,
    QuotaForecastCollector? quotaForecastCollector = null,
    ILocalUsageReader? localAnalysisReader = null,
    ILocalUsageReader? claudeAnalysisReader = null) : IDashboardService
{
    public static DashboardService CreateDefault(
        string? configuredCodexHome = null,
        bool incrementalIndexEnabled = true,
        string? defaultWorkspace = null,
        bool showSubagents = false,
        string? codexExecutable = null,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false,
        string? applicationDataDirectory = null)
    {
        var paths = CodexPaths.Discover(configuredCodexHome);
        return new DashboardService(
            new CodexAppServerClient(codexExecutable),
            new CodexLocalUsageReader(
                paths,
                incrementalIndexEnabled,
                defaultWorkspace,
                showSubagents,
                customRates,
                completeRateCatalog,
                applicationDataDirectory),
            new ClaudeCodeUsageReader(
                paths,
                defaultWorkspace,
                showSubagents,
                customRates,
                completeRateCatalog,
                applicationDataDirectory,
                incrementalIndexEnabled),
            new QuotaForecastCollector(new QuotaSampleStore(applicationDataDirectory)),
            new CodexLocalUsageReader(paths, incrementalIndexEnabled, null, showSubagents,
                customRates, completeRateCatalog, applicationDataDirectory),
            new ClaudeCodeUsageReader(paths, null, showSubagents, customRates,
                completeRateCatalog, applicationDataDirectory, incrementalIndexEnabled));
    }

    public async Task<DashboardSnapshot> LoadAsync(
        AgentRuntime runtime = AgentRuntime.Codex,
        CancellationToken cancellationToken = default)
    {
        if (runtime == AgentRuntime.ClaudeCode)
        {
            var claude = await ReadLocalSafelyAsync(
                claudeUsageReader,
                AgentRuntime.ClaudeCode,
                cancellationToken);
            return await WithForecastsAsync(
                ToDashboard(runtime, claude),
                new QuotaReading(claude.PrimaryQuota, QuotaForecastCollector.StatuslineSource),
                new QuotaReading(claude.SecondaryQuota, QuotaForecastCollector.StatuslineSource),
                cancellationToken);
        }

        var appServerTask = ReadAppServerSafelyAsync(cancellationToken);
        var localTask = ReadLocalSafelyAsync(localUsageReader, AgentRuntime.Codex, cancellationToken);
        await Task.WhenAll(appServerTask, localTask);

        var appServer = await appServerTask;
        var local = await localTask;
        var diagnostics = appServer.Diagnostics.Concat(local.Diagnostics).Distinct().ToList();
        var primary = Prefer(appServer.PrimaryQuota, local.PrimaryQuota);
        var secondary = Prefer(appServer.SecondaryQuota, local.SecondaryQuota);
        var snapshot = new DashboardSnapshot(
            runtime,
            DateTimeOffset.Now,
            MergeAccount(appServer.Account, local.Account),
            primary.Window,
            secondary.Window,
            local.Tokens,
            local.Tasks,
            local.DailyUsage,
            local.Projects,
            local.Tools,
            local.Skills,
            local.Sources,
            local.Models,
            local.Goals,
            local.TaskLifecycle,
            local.IndexStatus,
            diagnostics.Distinct().ToArray(), History: local.History, AnalysisData: local.AnalysisData);
        return await WithForecastsAsync(snapshot, primary, secondary, cancellationToken);
    }

    /// <summary>
    /// The app server is authoritative when it answers; local rollouts are the
    /// fallback. Which one it was has to travel with the reading, because a burn rate
    /// measured across a switch between the two would be comparing two different
    /// measurements rather than tracking one.
    /// </summary>
    private static QuotaReading Prefer(RateLimitWindow? appServer, RateLimitWindow? local) => appServer is not null
        ? new QuotaReading(appServer, QuotaForecastCollector.AppServerSource)
        : local is not null
            ? new QuotaReading(local, QuotaForecastCollector.LocalSource)
            : QuotaReading.None;

    private async Task<DashboardSnapshot> WithForecastsAsync(
        DashboardSnapshot snapshot,
        QuotaReading primary,
        QuotaReading secondary,
        CancellationToken cancellationToken)
    {
        if (quotaForecastCollector is null)
        {
            return snapshot;
        }

        try
        {
            var (primaryForecast, secondaryForecast) = await quotaForecastCollector.RecordAsync(
                snapshot.Runtime,
                primary,
                secondary,
                snapshot.RefreshedAt,
                cancellationToken);
            return snapshot with
            {
                PrimaryForecast = primaryForecast,
                SecondaryForecast = secondaryForecast
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A forecast is an extra on top of the dashboard; losing one is not a
            // reason to fail the refresh that carries every other figure.
            return snapshot;
        }
    }

    private static AccountSnapshot? MergeAccount(AccountSnapshot? appServer, AccountSnapshot? local)
    {
        if (appServer is null)
        {
            return local;
        }

        if (local is null)
        {
            return appServer;
        }

        return new AccountSnapshot(
            FirstNonBlank(appServer.AccountType, local.AccountType),
            FirstNonBlank(appServer.PlanType, local.PlanType),
            FirstNonBlank(appServer.Email, local.Email),
            appServer.IsAuthenticated || local.IsAuthenticated,
            // Only ever merges the two Codex sources; stated rather than defaulted so
            // the plan-price table cannot be chosen by accident.
            AgentRuntime.Codex);
    }

    private static string? FirstNonBlank(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static DashboardSnapshot ToDashboard(AgentRuntime runtime, LocalUsageSnapshot local) => new(
        runtime,
        DateTimeOffset.Now,
        local.Account,
        local.PrimaryQuota,
        local.SecondaryQuota,
        local.Tokens,
        local.Tasks,
        local.DailyUsage,
        local.Projects,
        local.Tools,
        local.Skills,
        local.Sources,
        local.Models,
        local.Goals,
        local.TaskLifecycle,
        local.IndexStatus,
        local.Diagnostics, History: local.History, AnalysisData: local.AnalysisData);

    public async Task<UsageAnalysisResult> QueryUsageAsync(UsageAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        UsageAnalysisQuery.Validate(request);
        var reader = request.Runtime switch
        {
            AgentRuntime.Codex => localAnalysisReader ?? localUsageReader,
            AgentRuntime.ClaudeCode => claudeAnalysisReader ?? claudeUsageReader ?? throw new InvalidOperationException("Claude Code 用量来源不可用。"),
            _ => throw new ArgumentException("不支持的工具。", nameof(request))
        };
        // Queries do not need account quota, and must not turn a failed local
        // read into a plausible empty result. The caller surfaces the failure.
        var local = await reader.ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (local.AnalysisData is null)
            throw new InvalidOperationException("本机用量读取未完成，暂不能查询明细。" + string.Join("；", local.Diagnostics));
        return UsageAnalysisQuery.Execute(local.AnalysisData, request, local.Diagnostics);
    }

    private async Task<AppServerSnapshot> ReadAppServerSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await appServerClient.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new AppServerSnapshot(
                null,
                null,
                null,
                [$"Codex app-server 读取失败：{exception.Message}"]);
        }
    }

    private static async Task<LocalUsageSnapshot> ReadLocalSafelyAsync(
        ILocalUsageReader? reader,
        AgentRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (reader is null)
        {
            return ToLocalSnapshot(DashboardSnapshot.Empty(runtime, $"{runtime} 本机数据适配器不可用"));
        }

        try
        {
            return await reader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToLocalSnapshot(DashboardSnapshot.Empty(runtime, $"本机数据读取失败：{exception.Message}"));
        }
    }

    private static LocalUsageSnapshot ToLocalSnapshot(DashboardSnapshot empty) => new(
        null,
        null,
        null,
        empty.Tokens,
        empty.Tasks,
        empty.DailyUsage,
        empty.Projects,
        empty.Tools,
        empty.Skills,
        empty.Sources,
        empty.Models,
        empty.Goals,
        empty.TaskLifecycle,
        empty.IndexStatus,
        empty.Diagnostics);
}
