using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class CodexLocalUsageReader(
    CodexPaths paths,
    bool incrementalIndexEnabled = true,
    string? defaultWorkspace = null,
    bool showSubagents = false,
    IReadOnlyList<ModelCreditRate>? customRates = null,
    bool completeRateCatalog = false,
    string? applicationDataDirectory = null) : ILocalUsageReader
{
    private readonly CodexSqliteReader _sqliteReader = new(paths, defaultWorkspace, showSubagents);
    private readonly CodexSessionReader _sessionReader = new(
        paths,
        incrementalIndexEnabled,
        indexDirectory: applicationDataDirectory,
        customRates: customRates,
        completeRateCatalog: completeRateCatalog);
    private readonly AutomationReader _automationReader = new(paths);
    private readonly GoalsReader _goalsReader = new(paths);
    private readonly LogHealthReader _logHealthReader = new(paths);

    public async Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var sqliteTask = ReadSqliteSafelyAsync(cancellationToken);
        var sessionTask = ReadSessionsSafelyAsync(cancellationToken);
        var goalsTask = ReadGoalsSafelyAsync(cancellationToken);
        await Task.WhenAll(sqliteTask, sessionTask, goalsTask);

        var sqlite = await sqliteTask;
        var session = await sessionTask;
        var goals = await goalsTask;
        var (scheduled, automationDiagnostic) = ReadScheduledSafely();
        var tasks = sqlite.Tasks.Concat(scheduled).ToArray();
        var diagnostics = sqlite.Diagnostics
            .Concat(session.Diagnostics)
            .Append(goals.Diagnostic)
            .Append(automationDiagnostic)
            .Append(ReadLogHealthSafely())
            .Distinct()
            .ToList();

        var hasSessionTokens = session.TokenEventCount > 0;
        var tokens = session.Tokens;
        var dailyUsage = session.DailyUsage;
        var models = session.Models;
        if (sqlite.Tokens.Lifetime.Tokens > 0)
        {
            diagnostics.Add("Token 主账本：仅使用历史 session JSONL；SQLite 线程累计值未合并，仅保留项目归属与覆盖信息");
        }

        var projects = ApportionProjectCost(sqlite.Projects, tokens.Lifetime, diagnostics);

        return new LocalUsageSnapshot(
            session.Account ?? sqlite.Account,
            session.PrimaryQuota ?? sqlite.PrimaryQuota,
            session.SecondaryQuota ?? sqlite.SecondaryQuota,
            tokens,
            tasks,
            dailyUsage,
            projects,
            session.Tools.Count > 0 ? session.Tools : sqlite.Tools,
            session.Skills.Count > 0 ? session.Skills : sqlite.Skills,
            sqlite.Sources,
            models,
            goals.Items,
            hasSessionTokens ? session.TaskLifecycle : sqlite.TaskLifecycle,
            session.IndexStatus,
            diagnostics.Distinct().ToArray());
    }

    /// <summary>
    /// Codex records which threads belong to which project in SQLite, but prices
    /// usage from session logs bucketed by date and model with no project key, so
    /// there is nothing to join on. Spreading the priced total across projects by
    /// token share is the closest honest answer; every result is flagged as an
    /// estimate so the UI never presents it as measured.
    /// </summary>
    private static IReadOnlyList<ProjectUsage> ApportionProjectCost(
        IReadOnlyList<ProjectUsage> projects,
        TokenPeriod lifetime,
        ICollection<string> diagnostics)
    {
        if (projects.Count == 0)
        {
            return projects;
        }

        var pricedCredits = lifetime.CreditsUsed;
        var attributedTokens = projects.Sum(project => Math.Max(0, project.Tokens));
        if (pricedCredits <= 0 || attributedTokens <= 0)
        {
            // Nothing priced, or nothing to weight by. Leaving cost null keeps the
            // projects panel honest instead of ranking everything at zero.
            diagnostics.Add("项目成本：Codex 本次没有可分摊的点数，项目成本显示为不可得");
            return projects;
        }

        diagnostics.Add(
            "项目成本：Codex 按线程 token 占比分摊本机点数总额（SQLite 记录项目归属，计价来自 session 日志，两者无法按项目关联），结果标记为估算");

        return projects
            .Select(project => project with
            {
                CreditsUsed = pricedCredits * (Math.Max(0, project.Tokens) / (double)attributedTokens),
                CostIsEstimated = true
            })
            .ToArray();
    }

    private async Task<LocalUsageSnapshot> ReadSqliteSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _sqliteReader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var empty = DashboardSnapshot.Empty(AgentRuntime.Codex, $"SQLite 数据源失败：{exception.Message}");
            return ToLocalSnapshot(empty);
        }
    }

    private async Task<SessionAnalytics> ReadSessionsSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _sessionReader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var empty = DashboardSnapshot.Empty(AgentRuntime.Codex, $"Session 数据源失败：{exception.Message}");
            return new SessionAnalytics(
                null,
                null,
                null,
                empty.Tokens,
                empty.DailyUsage,
                empty.Tools,
                empty.Skills,
                empty.Models,
                empty.TaskLifecycle,
                empty.IndexStatus,
                0,
                0,
                0,
                empty.Diagnostics);
        }
    }

    private async Task<(IReadOnlyList<GoalItem> Items, string Diagnostic)> ReadGoalsSafelyAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _goalsReader.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ([], $"Goals 数据源失败：{exception.Message}");
        }
    }

    private (IReadOnlyList<TaskItem> Items, string Diagnostic) ReadScheduledSafely()
    {
        try
        {
            var items = _automationReader.ReadScheduledTasks();
            return (items, $"Automations：已读取 {items.Count} 个任务 · {paths.AutomationsDirectory}");
        }
        catch (Exception exception)
        {
            return ([], $"Automations 数据源失败：{exception.Message}");
        }
    }

    private string ReadLogHealthSafely()
    {
        try
        {
            return _logHealthReader.ReadDiagnostic();
        }
        catch (Exception exception)
        {
            return $"日志健康数据源失败：{exception.Message}";
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
