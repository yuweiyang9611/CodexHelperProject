namespace CodexU.Core;

[Flags]
public enum UsageBreakdownFields
{
    None = 0,
    Input = 1,
    CachedInput = 2,
    Output = 4,
    ReasoningOutput = 8,
    CacheWrite5m = 32,
    CacheWrite1h = 64,
    All = Input | CachedInput | Output | ReasoningOutput | CacheWrite5m | CacheWrite1h
}

/// <summary>Null date bounds query all retained dates. UI defaults to the last 30 days.</summary>
public sealed record UsageAnalysisRequest(
    AgentRuntime Runtime = AgentRuntime.Codex,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Model = null,
    string? Project = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>Disjoint, deduplicated contribution. Null session/breakdown identifies an old aggregate remainder.</summary>
public sealed record UsageAnalysisEntry(
    DateOnly Date,
    string? SessionId,
    string? ParentSessionId,
    string? Title,
    string? Project,
    string Model,
    string Feature,
    long Tokens,
    TokenBreakdown? Breakdown,
    double? CreditsUsed,
    ModelCreditRate? Rate,
    string Source,
    string? Explanation = null,
    IReadOnlyList<string>? AvailableBreakdownFields = null);

/// <summary>Historical whole-day estimate, kept separately from repriced detail; never an additive cost.</summary>
public sealed record UsageLegacyEstimate(DateOnly Date, long Tokens, double CreditsUsed, string Explanation);

public sealed record UsageAnalysisData(
    IReadOnlyList<UsageAnalysisEntry> Entries,
    IReadOnlyList<UsageLegacyEstimate> LegacyEstimates);

public sealed record UsageAnalysisTotals(
    long Tokens,
    TokenBreakdown Breakdown,
    long BreakdownTokens,
    double? CreditsUsed,
    long RatedTokens,
    long UnratedTokens,
    long UnattributedTokens,
    IReadOnlyList<string>? AvailableBreakdownFields = null);

public sealed record UsageAnalysisGroup(string Id, string Label, UsageAnalysisTotals Totals);
public sealed record UsageAnalysisDay(DateOnly Date, UsageAnalysisTotals Totals);
public sealed record UsageProjectOption(string Id, string Label);
public sealed record UsageMissingRate(string Model, DateOnly Date, long Tokens);

/// <summary>One actual source session, with its own contributions only.</summary>
public sealed record UsageSessionMember(
    string Id,
    string? ParentSessionId,
    string? Title,
    string? Project,
    DateOnly From,
    DateOnly To,
    UsageAnalysisTotals Totals,
    IReadOnlyList<string> Sources,
    IReadOnlyList<UsageAnalysisEntry> Contributions);

/// <summary>A group includes every selected descendant once; members carry only their own contribution.</summary>
public sealed record UsageSessionGroup(
    string Id,
    string? Title,
    DateOnly From,
    DateOnly To,
    UsageAnalysisTotals Totals,
    IReadOnlyList<UsageSessionMember> Members);

public sealed record UsageAnalysisResult(
    AgentRuntime Runtime,
    DateOnly? From,
    DateOnly? To,
    DateOnly? AvailableFrom,
    DateOnly? AvailableTo,
    UsageAnalysisTotals Totals,
    IReadOnlyList<UsageAnalysisDay> Days,
    IReadOnlyList<UsageAnalysisGroup> Models,
    IReadOnlyList<UsageAnalysisGroup> Features,
    IReadOnlyList<UsageAnalysisGroup> Projects,
    IReadOnlyList<UsageSessionGroup> Sessions,
    int SessionCount,
    int Page,
    int PageSize,
    IReadOnlyList<UsageAnalysisEntry> Unattributed,
    IReadOnlyList<UsageLegacyEstimate> LegacyEstimates,
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<UsageProjectOption> AvailableProjects,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<UsageMissingRate>? MissingRates = null);
