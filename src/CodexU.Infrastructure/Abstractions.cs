using CodexU.Core;

namespace CodexU.Infrastructure;

public interface IAppServerClient
{
    Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public interface ILocalUsageReader
{
    Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    Task<DashboardSnapshot> LoadAsync(
        AgentRuntime runtime = AgentRuntime.Codex,
        CancellationToken cancellationToken = default);

    async Task<UsageAnalysisResult> QueryUsageAsync(UsageAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(request.Runtime, cancellationToken);
        return UsageAnalysisQuery.Execute(snapshot.AnalysisData, request, snapshot.Diagnostics);
    }
}

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        bool includePrereleases,
        bool force,
        CancellationToken cancellationToken = default);
}
