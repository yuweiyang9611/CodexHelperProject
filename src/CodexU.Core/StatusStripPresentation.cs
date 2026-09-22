namespace CodexU.Core;

public enum StatusStripVisualState
{
    Pending,
    Healthy,
    Degraded,
    Unavailable,
    Refreshing,
    Failed
}

public sealed record StatusStripQuotaPresentation(
    string Text,
    double? ProgressPercent,
    string AccessibleText,
    string ResetText = "刷新时间未知",
    DateTimeOffset? ResetsAt = null)
{
    public bool IsAvailable => ProgressPercent is not null;
}

public sealed record StatusStripTokenPresentation(
    string Text,
    string AccessibleText,
    DataQuality Quality)
{
    public bool IsAvailable => Quality != DataQuality.Unavailable;

    public bool IsDegraded => Quality is DataQuality.Partial or DataQuality.Approximate;
}

public sealed record StatusStripPresentation(
    bool HasSnapshot,
    bool ShowTodayTokens,
    string RuntimeTitle,
    string PrimaryLabel,
    string SecondaryLabel,
    string PrimaryDetailLabel,
    string SecondaryDetailLabel,
    StatusStripQuotaPresentation PrimaryQuota,
    StatusStripQuotaPresentation SecondaryQuota,
    StatusStripTokenPresentation Today,
    StatusStripTokenPresentation SevenDays,
    StatusStripTokenPresentation Lifetime,
    string TodoText,
    string TodoAccessibleText,
    StatusStripVisualState VisualState,
    string StateGlyph,
    string StatusText,
    string StatusToolTip,
    bool CanRefresh);

public sealed class StatusStripPresenter
{
    private DashboardSnapshot? _snapshot;
    private int? _openTodoCount;
    private StatusStripVisualState _refreshState = StatusStripVisualState.Pending;
    private string? _refreshError;
    private DashboardSnapshot? _refreshBaseline;
    private long _refreshGeneration;

    public StatusStripPresenter(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    public DashboardSnapshot? Snapshot => _snapshot;

    public StatusStripPresentation Current => Build();

    public StatusStripPresentation ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Build();
    }

    public StatusStripPresentation UpdateSnapshot(DashboardSnapshot snapshot, int? openTodoCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _openTodoCount = NormalizeTodoCount(openTodoCount);
        if (_refreshState is StatusStripVisualState.Refreshing or StatusStripVisualState.Failed
            && !ReferenceEquals(snapshot, _refreshBaseline))
        {
            ClearRefreshState();
        }

        return Build();
    }

    public StatusStripPresentation UpdateTodoCount(int? openTodoCount)
    {
        _openTodoCount = NormalizeTodoCount(openTodoCount);
        return Build();
    }

    public long BeginRefresh()
    {
        _refreshGeneration++;
        _refreshBaseline = _snapshot;
        _refreshError = null;
        _refreshState = StatusStripVisualState.Refreshing;
        return _refreshGeneration;
    }

    public StatusStripPresentation CompleteRefresh(long generation, DashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (generation != _refreshGeneration || _refreshState != StatusStripVisualState.Refreshing)
        {
            return Build();
        }

        if (ReferenceEquals(snapshot, _refreshBaseline))
        {
            _refreshState = StatusStripVisualState.Failed;
            _refreshError = "刷新完成，但本机数据服务未返回新快照。";
            return Build();
        }

        _snapshot = snapshot;
        ClearRefreshState();
        return Build();
    }

    public StatusStripPresentation FailRefresh(long generation, string message)
    {
        if (generation != _refreshGeneration || _refreshState != StatusStripVisualState.Refreshing)
        {
            return Build();
        }

        _refreshState = StatusStripVisualState.Failed;
        _refreshError = string.IsNullOrWhiteSpace(message) ? "刷新失败。" : message.Trim();
        return Build();
    }

    private StatusStripPresentation Build()
    {
        const string primaryLabel = "5h 剩余  ";
        const string secondaryLabel = "7d 剩余  ";
        const string primaryDetailLabel = "5 小时剩余额度";
        const string secondaryDetailLabel = "7 天剩余额度";

        if (_snapshot is null)
        {
            var unavailableToday = UnavailableToken("今日");
            var unavailableWeek = UnavailableToken("近 7 天");
            var unavailableLifetime = UnavailableToken("累计");
            var pendingState = _refreshState == StatusStripVisualState.Refreshing
                ? StatusStripVisualState.Refreshing
                : _refreshState == StatusStripVisualState.Failed
                    ? StatusStripVisualState.Failed
                    : StatusStripVisualState.Pending;
            var pendingText = pendingState switch
            {
                StatusStripVisualState.Refreshing => "正在读取本机数据…",
                StatusStripVisualState.Failed => _refreshError ?? "刷新失败。",
                _ => "尚未读取本机数据"
            };
            return new StatusStripPresentation(
                false,
                false,
                "Codex 使用状态",
                primaryLabel,
                secondaryLabel,
                primaryDetailLabel,
                secondaryDetailLabel,
                UnavailableQuota(primaryDetailLabel),
                UnavailableQuota(secondaryDetailLabel),
                unavailableToday,
                unavailableWeek,
                unavailableLifetime,
                _openTodoCount?.ToString() ?? "--",
                FormatTodoAccessibleText(_openTodoCount),
                pendingState,
                GlyphFor(pendingState),
                pendingText,
                pendingText,
                pendingState != StatusStripVisualState.Refreshing);
        }

        var primary = FormatQuota(_snapshot.PrimaryQuota, primaryDetailLabel);
        var secondary = FormatQuota(_snapshot.SecondaryQuota, secondaryDetailLabel);
        var today = FormatToken("今日", _snapshot.Tokens.Today);
        var sevenDays = FormatToken("近 7 天", _snapshot.Tokens.SevenDays);
        var lifetime = FormatToken("累计", _snapshot.Tokens.Lifetime);
        var snapshotHealth = BuildSnapshotHealth(primary, secondary);
        var visualState = snapshotHealth.State;
        var statusText = snapshotHealth.Text;
        var statusToolTip = snapshotHealth.ToolTip;

        if (_refreshState == StatusStripVisualState.Refreshing)
        {
            visualState = StatusStripVisualState.Refreshing;
            statusText = $"正在刷新… 上次更新于 {_snapshot.RefreshedAt.ToLocalTime():HH:mm}";
            statusToolTip = "正在读取本机数据；当前继续显示上次成功快照。";
        }
        else if (_refreshState == StatusStripVisualState.Failed)
        {
            visualState = StatusStripVisualState.Failed;
            statusText = _refreshError ?? "刷新失败。";
            statusToolTip = $"{statusText} 当前继续显示上次成功快照。";
        }

        return new StatusStripPresentation(
            true,
            false,
            _snapshot.Runtime == AgentRuntime.ClaudeCode
                ? "Claude Code 使用状态"
                : "Codex 使用状态",
            primaryLabel,
            secondaryLabel,
            primaryDetailLabel,
            secondaryDetailLabel,
            primary,
            secondary,
            today,
            sevenDays,
            lifetime,
            _openTodoCount?.ToString() ?? "--",
            FormatTodoAccessibleText(_openTodoCount),
            visualState,
            GlyphFor(visualState),
            statusText,
            statusToolTip,
            visualState != StatusStripVisualState.Refreshing);
    }

    private SnapshotHealth BuildSnapshotHealth(
        StatusStripQuotaPresentation primary,
        StatusStripQuotaPresentation secondary)
    {
        var unavailable = new List<string>();
        if (!primary.IsAvailable) unavailable.Add("5 小时额度");
        if (!secondary.IsAvailable) unavailable.Add("7 天额度");
        var refreshedAt = _snapshot!.RefreshedAt.ToLocalTime();
        return unavailable.Count > 0
            ? new SnapshotHealth(
                StatusStripVisualState.Unavailable,
                $"更新于 {refreshedAt:HH:mm} · 部分额度不可用",
                $"额度来源未提供：{string.Join("、", unavailable)}。请在主界面查看数据来源状态。")
            : new SnapshotHealth(
                StatusStripVisualState.Healthy,
                $"额度更新于 {refreshedAt:HH:mm}",
                "账户额度独立于本机用量统计与筛选。");
    }

    private static StatusStripQuotaPresentation FormatQuota(
        RateLimitWindow? quota,
        string label)
    {
        if (quota is null)
        {
            return UnavailableQuota(label);
        }

        if (!double.IsFinite(quota.UsedPercent))
        {
            return UnavailableQuota(label);
        }

        var rawValue = quota.RemainingPercent;
        if (!double.IsFinite(rawValue))
        {
            return UnavailableQuota(label);
        }

        var value = Math.Clamp(rawValue, 0d, 100d);
        var text = $"{Math.Round(value)}%";
        var resetText = quota.ResetsAt is { } resetsAt
            ? $"{resetsAt.ToLocalTime():M/d HH:mm} 刷新"
            : "刷新时间未知";
        return new StatusStripQuotaPresentation(text, value, $"{label} {text}，{resetText}", resetText, quota.ResetsAt);
    }

    private static StatusStripQuotaPresentation UnavailableQuota(string label) =>
        new("--", null, $"{label}不可用");

    private static StatusStripTokenPresentation FormatToken(string label, TokenPeriod period)
    {
        var tokens = Math.Max(0, period.Tokens);
        var compact = Compact(tokens);
        return period.Quality switch
        {
            DataQuality.Unavailable => UnavailableToken(label),
            DataQuality.Partial => new(
                $"部分 {compact}",
                $"{label} Token 为部分数据，{tokens:N0}",
                period.Quality),
            DataQuality.Approximate => new(
                $"约 {compact}",
                $"{label} Token 为估算数据，约 {tokens:N0}",
                period.Quality),
            _ => new(compact, $"{label} Token {tokens:N0}", period.Quality)
        };
    }

    private static StatusStripTokenPresentation UnavailableToken(string label) =>
        new("--", $"{label} Token 不可用", DataQuality.Unavailable);

    private static string FormatTodoAccessibleText(int? openTodoCount) =>
        openTodoCount is null ? "未完成待办数量未知" : $"未完成待办 {openTodoCount.Value}";

    private static string GlyphFor(StatusStripVisualState state) => state switch
    {
        StatusStripVisualState.Healthy => "✓",
        StatusStripVisualState.Degraded => "≈",
        StatusStripVisualState.Unavailable or StatusStripVisualState.Failed => "!",
        StatusStripVisualState.Refreshing => "⟳",
        _ => "…"
    };

    private static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0")
    };

    private static int? NormalizeTodoCount(int? openTodoCount) =>
        openTodoCount is null ? null : Math.Max(0, openTodoCount.Value);

    private void ClearRefreshState()
    {
        _refreshState = StatusStripVisualState.Healthy;
        _refreshError = null;
        _refreshBaseline = null;
    }

    private sealed record SnapshotHealth(
        StatusStripVisualState State,
        string Text,
        string ToolTip);
}
