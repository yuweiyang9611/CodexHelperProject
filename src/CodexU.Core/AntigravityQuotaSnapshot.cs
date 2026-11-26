namespace CodexU.Core;

public enum AntigravityQuotaStatus { Ready, NotInstalled, UnsupportedVersion, Unavailable, NoQuota }

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
