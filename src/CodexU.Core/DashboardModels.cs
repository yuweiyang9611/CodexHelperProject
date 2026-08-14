namespace CodexU.Core;

public enum AgentRuntime
{
    Codex,
    ClaudeCode
}

public enum DataQuality
{
    Detailed,
    Partial,
    Approximate,
    Unavailable
}

public enum TaskColumnKind
{
    Active,
    Pending,
    Scheduled,
    Done
}

public sealed record RateLimitWindow(
    double UsedPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt,
    /// <summary>
    /// When the figure was actually measured, where that is knowable — the
    /// statusline snapshot's write time, say. A burn rate divides consumption by
    /// elapsed time, so dating a reading by the refresh that happened to pick it up
    /// would understate the rate whenever a stale file is re-read. Null means the
    /// source cannot say, and the reader falls back to the refresh time.
    /// </summary>
    DateTimeOffset? MeasuredAt = null)
{
    public double RemainingPercent => double.IsFinite(UsedPercent)
        ? Math.Clamp(100d - UsedPercent, 0d, 100d)
        : 0d;
}

public static class RateLimitWindowClassifier
{
    private const int OneDayMinutes = 24 * 60;

    public static (RateLimitWindow? FiveHour, RateLimitWindow? SevenDay) Classify(
        RateLimitWindow? namedPrimary,
        RateLimitWindow? namedSecondary)
    {
        RateLimitWindow? fiveHour = null;
        RateLimitWindow? sevenDay = null;
        var candidates = new[]
        {
            (Window: namedPrimary, IsNamedPrimary: true),
            (Window: namedSecondary, IsNamedPrimary: false)
        };

        foreach (var candidate in candidates.Where(item => item.Window?.WindowDurationMinutes is > 0))
        {
            if (candidate.Window!.WindowDurationMinutes <= OneDayMinutes)
            {
                fiveHour ??= candidate.Window;
            }
            else
            {
                sevenDay ??= candidate.Window;
            }
        }

        foreach (var candidate in candidates.Where(item => item.Window is not null
                                                            && item.Window.WindowDurationMinutes is not > 0))
        {
            if (candidate.IsNamedPrimary)
            {
                fiveHour ??= candidate.Window;
            }
            else
            {
                sevenDay ??= candidate.Window;
            }
        }

        return (fiveHour, sevenDay);
    }
}

public sealed record AccountSnapshot(
    string? AccountType,
    string? PlanType,
    string? Email,
    bool IsAuthenticated,
    AgentRuntime Runtime = AgentRuntime.Codex)
{
    // Plan names collide across vendors — "pro" is US$20 on Claude and US$200 on
    // ChatGPT — so the runtime that reported the plan has to travel with it.
    public double? SuggestedMonthlySubscriptionAmount =>
        SubscriptionPricing.InferMonthlyAmount(PlanType, Runtime);
}

public sealed record TokenBreakdown(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long TotalTokens,
    long CacheWrite5mTokens = 0,
    long CacheWrite1hTokens = 0)
{
    public static TokenBreakdown Zero { get; } = new(0, 0, 0, 0, 0);

    public long BillableCachedInputTokens => Math.Min(Math.Max(CachedInputTokens, 0), Math.Max(InputTokens, 0));

    // Cache writes are a third slice of InputTokens, alongside cache reads and
    // plain input. Sources that do not report the split leave both at zero, which
    // keeps the whole remainder priced as plain input exactly as before.
    private long CacheWriteBudget => Math.Max(0, Math.Max(InputTokens, 0) - BillableCachedInputTokens);

    public long BillableCacheWrite5mTokens =>
        Math.Min(Math.Max(CacheWrite5mTokens, 0), CacheWriteBudget);

    public long BillableCacheWrite1hTokens => Math.Min(
        Math.Max(CacheWrite1hTokens, 0),
        Math.Max(0, CacheWriteBudget - BillableCacheWrite5mTokens));

    public long BillableCacheWriteTokens => BillableCacheWrite5mTokens + BillableCacheWrite1hTokens;

    public long UncachedInputTokens =>
        Math.Max(0, InputTokens - BillableCachedInputTokens - BillableCacheWriteTokens);

    public long VisibleTotalTokens => Math.Max(TotalTokens, Math.Max(0, InputTokens) + Math.Max(0, OutputTokens));

    public TokenBreakdown Add(TokenBreakdown other) => new(
        InputTokens + other.InputTokens,
        CachedInputTokens + other.CachedInputTokens,
        OutputTokens + other.OutputTokens,
        ReasoningOutputTokens + other.ReasoningOutputTokens,
        TotalTokens + other.TotalTokens,
        CacheWrite5mTokens + other.CacheWrite5mTokens,
        CacheWrite1hTokens + other.CacheWrite1hTokens);

    public TokenBreakdown PositiveDelta(TokenBreakdown previous)
    {
        // Codex counters are cumulative within a process, but restart from zero when
        // the process/session counter is reset. Treat a lower total as a new baseline
        // so the first sample after a restart is not silently discarded.
        var currentComparableTotal = VisibleTotalTokens;
        var previousComparableTotal = previous.VisibleTotalTokens;
        if (currentComparableTotal > 0
            && previousComparableTotal > 0
            && currentComparableTotal < previousComparableTotal)
        {
            return new TokenBreakdown(
                Math.Max(0, InputTokens),
                Math.Max(0, CachedInputTokens),
                Math.Max(0, OutputTokens),
                Math.Max(0, ReasoningOutputTokens),
                Math.Max(0, TotalTokens),
                Math.Max(0, CacheWrite5mTokens),
                Math.Max(0, CacheWrite1hTokens));
        }

        return new TokenBreakdown(
            Math.Max(0, InputTokens - previous.InputTokens),
            Math.Max(0, CachedInputTokens - previous.CachedInputTokens),
            Math.Max(0, OutputTokens - previous.OutputTokens),
            Math.Max(0, ReasoningOutputTokens - previous.ReasoningOutputTokens),
            Math.Max(0, TotalTokens - previous.TotalTokens),
            Math.Max(0, CacheWrite5mTokens - previous.CacheWrite5mTokens),
            Math.Max(0, CacheWrite1hTokens - previous.CacheWrite1hTokens));
    }
}

public sealed record TokenPeriod(
    long Tokens,
    TokenBreakdown Breakdown,
    double CreditsUsed,
    long UnratedTokens,
    IReadOnlyList<ModelCreditUsage> CreditsByModel,
    DataQuality Quality);

public sealed record TokenSummary(
    TokenPeriod Today,
    TokenPeriod SevenDays,
    TokenPeriod Month,
    TokenPeriod Lifetime);

public sealed record TaskItem(
    string Id,
    string Title,
    string Project,
    DateTimeOffset? UpdatedAt,
    long? Tokens,
    TaskColumnKind Kind,
    string? Detail = null);

public sealed record DailyUsage(
    DateOnly Date,
    long Tokens,
    double CreditsUsed,
    DataQuality Quality);

public sealed record ProjectUsage(
    string Id,
    string Name,
    string? FullPath,
    long Tokens,
    int ThreadCount,
    DateTimeOffset? LastActiveAt,
    string? Branch,
    double? CreditsUsed,
    DataQuality Quality,
    bool CostIsEstimated = false)
{
    /// <summary>
    /// Null cost means unknown, never free. Codex attributes threads and tokens in
    /// SQLite but prices usage from session logs keyed only by date and model, so
    /// the two cannot be joined per project — its cost is apportioned by token
    /// share and flagged. Showing an unpriced project as US$0.00 beside real tokens
    /// reads as "this cost nothing", which is the opposite of "we cannot tell".
    /// </summary>
    public bool HasKnownCost => CreditsUsed is > 0;
}

public sealed record RankedUsage(
    string Id,
    string Name,
    int Count,
    long? EstimatedTokens,
    double? CreditsUsed,
    string? Category = null);

public sealed record DashboardSnapshot(
    AgentRuntime Runtime,
    DateTimeOffset RefreshedAt,
    AccountSnapshot? Account,
    RateLimitWindow? PrimaryQuota,
    RateLimitWindow? SecondaryQuota,
    TokenSummary Tokens,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<DailyUsage> DailyUsage,
    IReadOnlyList<ProjectUsage> Projects,
    IReadOnlyList<RankedUsage> Tools,
    IReadOnlyList<RankedUsage> Skills,
    IReadOnlyList<RankedUsage> Sources,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyList<GoalItem> Goals,
    TaskLifecycleStats TaskLifecycle,
    IndexStatus IndexStatus,
    IReadOnlyList<string> Diagnostics,
    QuotaForecast? PrimaryForecast = null,
    QuotaForecast? SecondaryForecast = null)
{
    public static DashboardSnapshot Empty(AgentRuntime runtime, params string[] diagnostics) => new(
        runtime,
        DateTimeOffset.Now,
        null,
        null,
        null,
        new TokenSummary(
            new TokenPeriod(0, TokenBreakdown.Zero, 0, 0, [], DataQuality.Unavailable),
            new TokenPeriod(0, TokenBreakdown.Zero, 0, 0, [], DataQuality.Unavailable),
            new TokenPeriod(0, TokenBreakdown.Zero, 0, 0, [], DataQuality.Unavailable),
            new TokenPeriod(0, TokenBreakdown.Zero, 0, 0, [], DataQuality.Unavailable)),
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        TaskLifecycleStats.Empty,
        new IndexStatus(false, 0, 0, 0, 0, null),
        diagnostics);
}

/// <summary>
/// One runtime's half of a combined read, carrying whether the read actually
/// succeeded.
///
/// A failed read and an absent runtime both arrive as <see cref="DashboardSnapshot.Empty"/>
/// — every period Unavailable, no account, zero tokens — and the difference decides
/// whether a combined total is complete. A runtime the user does not have is simply
/// not part of the total; a runtime whose read threw has usage that belongs in the
/// total and is missing from it, which has to be said out loud rather than inferred
/// from a quality flag that cannot tell the two apart.
/// </summary>
public sealed record RuntimeReadResult(
    DashboardSnapshot Snapshot,
    bool ReadFailed,
    string? FailureMessage = null);

/// <summary>
/// Both runtimes read in one pass, deliberately unmerged on the wire. Every rule for
/// what may and may not be combined lives in one place in the frontend, where it is
/// directly testable, rather than being baked into a merged snapshot no caller could
/// take apart again.
/// </summary>
public sealed record CombinedSnapshots(
    RuntimeReadResult Codex,
    RuntimeReadResult ClaudeCode);

public sealed record LocalUsageSnapshot(
    AccountSnapshot? Account,
    RateLimitWindow? PrimaryQuota,
    RateLimitWindow? SecondaryQuota,
    TokenSummary Tokens,
    IReadOnlyList<TaskItem> Tasks,
    IReadOnlyList<DailyUsage> DailyUsage,
    IReadOnlyList<ProjectUsage> Projects,
    IReadOnlyList<RankedUsage> Tools,
    IReadOnlyList<RankedUsage> Skills,
    IReadOnlyList<RankedUsage> Sources,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyList<GoalItem> Goals,
    TaskLifecycleStats TaskLifecycle,
    IndexStatus IndexStatus,
    IReadOnlyList<string> Diagnostics);

public sealed record AppServerSnapshot(
    AccountSnapshot? Account,
    RateLimitWindow? PrimaryQuota,
    RateLimitWindow? SecondaryQuota,
    IReadOnlyList<string> Diagnostics);
