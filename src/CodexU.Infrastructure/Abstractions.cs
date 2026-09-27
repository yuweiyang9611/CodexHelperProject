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
    /// <summary>Discard reconstructible query state after local maintenance. Refresh also invalidates it.</summary>
    void InvalidateUsageAnalysis() { }

    Task<DashboardSnapshot> LoadAsync(
        AgentRuntime runtime = AgentRuntime.Codex,
        CancellationToken cancellationToken = default);

    /// <summary>Query the current read revision; call LoadAsync or invalidate after source changes.</summary>
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
