namespace CodexU.Core;

/// <summary>
/// ModelInvoked: the CLI answered /usage by running a model turn, which spends the
/// account's quota; queries stay stopped until the CLI version changes.
/// Disabled: the user turned Antigravity quota reads off; the CLI is never started.
/// </summary>
public enum AntigravityQuotaStatus { Ready, NotInstalled, UnsupportedVersion, Unavailable, NoQuota, ModelInvoked, Disabled }

public sealed record AntigravityQuotaSnapshot(
    AntigravityQuotaStatus Status,
    IReadOnlyList<AntigravityQuotaGroup> Groups,
    DateTimeOffset? ObservedAt = null);

public sealed record AntigravityQuotaGroup(string Name, IReadOnlyList<AntigravityQuotaBucket> Buckets);

public sealed record AntigravityQuotaBucket(
    string Label,
    double? RemainingPercent,
    double? RemainingAmount,
    DateTimeOffset? ResetsAt,
    bool Disabled);
