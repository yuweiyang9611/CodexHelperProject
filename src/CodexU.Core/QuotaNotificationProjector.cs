using System.Security.Cryptography;
using System.Text;

namespace CodexU.Core;

/// <summary>
/// A host-neutral notification produced from a dashboard snapshot. Native hosts
/// decide how to display it; all quota decisions and de-duplication stay here. Its
/// ID identifies the logical alert and remains stable across host-process restarts.
/// </summary>
public sealed record QuotaNotification(
    string Id,
    string Title,
    string Body);

/// <summary>
/// Projects quota, forecast, monthly amount, and rate-coverage notifications from
/// snapshots. The projector is intentionally stateful: it owns the once-per-window
/// and once-per-month de-duplication shared by every desktop host.
/// </summary>
public sealed class QuotaNotificationProjector
{
    private readonly object _gate = new();
    private readonly QuotaAlertState _quotaAlerts = new();
    private readonly HashSet<string> _monthlyKeys = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly IQuotaWindowIdentityProvider _windowIdentities;

    public QuotaNotificationProjector(
        Func<DateTimeOffset>? clock = null,
        IQuotaWindowIdentityProvider? windowIdentities = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        _windowIdentities = windowIdentities ?? new TimeBucketQuotaWindowIdentityProvider();
    }

    public IReadOnlyList<QuotaNotification> Project(
        DashboardSnapshot snapshot,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            // A rollover re-arms that window's alerts, so both windows must be
            // observed even while presentation is disabled. Otherwise a whole
            // rollover can pass unnoticed and an old fired key can silence the
            // first alert after notifications are turned back on.
            _quotaAlerts.ObserveWindow(snapshot.Runtime, "5 小时", snapshot.PrimaryQuota);
            _quotaAlerts.ObserveWindow(snapshot.Runtime, "7 天", snapshot.SecondaryQuota);
            var primaryWindowKey = ObserveWindowIdentity(
                snapshot.Runtime,
                "5 小时",
                snapshot.PrimaryQuota);
            var secondaryWindowKey = ObserveWindowIdentity(
                snapshot.Runtime,
                "7 天",
                snapshot.SecondaryQuota);

            if (!settings.NotificationsEnabled)
            {
                return [];
            }

            var notifications = new List<QuotaNotification>(7);

            AddQuotaRefresh(notifications, snapshot.Runtime, snapshot.PrimaryQuota);
            AddBelowThreshold(
                notifications,
                snapshot.Runtime,
                "5 小时",
                snapshot.PrimaryQuota,
                primaryWindowKey,
                settings.FiveHourAlertPercent);
            AddBelowThreshold(
                notifications,
                snapshot.Runtime,
                "7 天",
                snapshot.SecondaryQuota,
                secondaryWindowKey,
                settings.SevenDayAlertPercent);
            AddForecast(
                notifications,
                snapshot.Runtime,
                "5 小时",
                snapshot.PrimaryQuota,
                primaryWindowKey,
                snapshot.PrimaryForecast,
                settings.FiveHourAlertPercent,
                QuotaForecastAlert.PrimaryLeadTime,
                settings.QuotaForecastAlertsEnabled);
            AddForecast(
                notifications,
                snapshot.Runtime,
                "7 天",
                snapshot.SecondaryQuota,
                secondaryWindowKey,
                snapshot.SecondaryForecast,
                settings.SevenDayAlertPercent,
                QuotaForecastAlert.SecondaryLeadTime,
                settings.QuotaForecastAlertsEnabled);
            AddMonthlyAmount(notifications, snapshot, settings);
            AddRateCoverage(notifications, snapshot, settings);

            return notifications;
        }
    }

    private void AddQuotaRefresh(
        ICollection<QuotaNotification> notifications,
        AgentRuntime runtime,
        RateLimitWindow? quota)
    {
        if (_quotaAlerts.ObserveReset(runtime, quota) is not { } kind)
        {
            return;
        }

        notifications.Add(Create(
            kind == QuotaRefreshKind.Refreshed ? "quota-refreshed" : "quota-rescheduled",
            $"{runtime}|{quota!.ResetsAt!.Value.UtcDateTime.Ticks}",
            kind == QuotaRefreshKind.Refreshed
                ? "codexU 额度已刷新"
                : "codexU 刷新时间已更新",
            $"{runtime} 下一次 5 小时额度刷新：{quota.ResetsAt.Value.ToLocalTime():g}"));
    }

    private void AddBelowThreshold(
        ICollection<QuotaNotification> notifications,
        AgentRuntime runtime,
        string label,
        RateLimitWindow? quota,
        string? windowKey,
        int threshold)
    {
        if (quota is null
            || !_quotaAlerts.TryAnnounce(
                runtime,
                label,
                QuotaAlertKind.BelowThreshold,
                quota.RemainingPercent <= threshold))
        {
            return;
        }

        var resetsAt = quota.ResetsAt is { } reset
            ? $"，将在 {reset.ToLocalTime():g} 重置"
            : string.Empty;
        notifications.Add(Create(
            "quota-below-threshold",
            windowKey!,
            "codexU 额度提醒",
            $"{runtime} {label}额度剩余 {Math.Round(quota.RemainingPercent)}%{resetsAt}。"));
    }

    private void AddForecast(
        ICollection<QuotaNotification> notifications,
        AgentRuntime runtime,
        string label,
        RateLimitWindow? quota,
        string? windowKey,
        QuotaForecast? forecast,
        int alertThresholdPercent,
        TimeSpan leadTime,
        bool enabled)
    {
        if (!enabled
            || !_quotaAlerts.TryAnnounce(
                runtime,
                label,
                QuotaAlertKind.Forecast,
                QuotaForecastAlert.ShouldWarn(
                    quota,
                    forecast,
                    alertThresholdPercent,
                    leadTime)))
        {
            return;
        }

        notifications.Add(Create(
            "quota-forecast",
            windowKey!,
            "codexU 额度预警",
            $"{runtime} 按最近 {FormatDuration(forecast!.MeasuredOver)}的用量，{label}额度预计还有 "
            + $"{FormatDuration(forecast.TimeToExhaustion)}耗尽（约 {forecast.ExhaustsAt.ToLocalTime():g}），早于本轮重置。"));
    }

    private void AddMonthlyAmount(
        ICollection<QuotaNotification> notifications,
        DashboardSnapshot snapshot,
        AppSettings settings)
    {
        if (settings.MonthlyAmountAlert <= 0)
        {
            return;
        }

        var now = _clock();
        var today = DateOnly.FromDateTime(now.Date);
        var entries = snapshot.AnalysisData?.Entries
            .Where(entry => entry.Date.Year == today.Year && entry.Date.Month == today.Month && entry.Date <= today)
            .ToArray();
        // Analysis entries are disjoint and priced at their usage date. The old
        // month snapshot may contain a whole-day estimate that overlaps them.
        var credits = entries?.Sum(entry => entry.CreditsUsed ?? 0) ?? snapshot.Tokens.Month.CreditsUsed;
        var partial = entries?.Any(entry => entry.Tokens > 0 && entry.CreditsUsed is null)
            ?? snapshot.Tokens.Month.UnratedTokens > 0;
        var amount = UsageCredits.ToAmount(credits, settings.AmountPerThousandCredits);
        if (amount < settings.MonthlyAmountAlert)
        {
            return;
        }

        var key = FormattableString.Invariant(
            $"monthly-amount:{snapshot.Runtime}:{now:yyyy-MM}:{settings.MonthlyAmountAlert:0.##}");
        if (!_monthlyKeys.Add(key))
        {
            return;
        }

        notifications.Add(Create(
            "monthly-amount",
            key,
            "codexU 本月金额提醒",
            $"{snapshot.Runtime} 本月 API 等效金额{(partial ? "（已核算部分）" : "")}已达到 US${amount:N2}，超过提醒值 US${settings.MonthlyAmountAlert:N2}。"));
    }

    private void AddRateCoverage(
        ICollection<QuotaNotification> notifications,
        DashboardSnapshot snapshot,
        AppSettings settings)
    {
        var month = snapshot.Tokens.Month;
        if (month.Tokens <= 0 || settings.MinimumRateCoverageAlertPercent <= 0)
        {
            return;
        }

        // UnratedTokens also includes observations without a model and legacy
        // totals without pricing detail. Adding a rate cannot repair those.
        // Check confirmed model slices against the catalog on their usage date.
        var now = _clock();
        var today = DateOnly.FromDateTime(now.Date);
        var usages = snapshot.DailyUsage
            .Where(day => day.Date.Year == today.Year && day.Date.Month == today.Month && day.Date <= today
                && day.Source is "live" or "retained" or "legacy")
            .SelectMany(day => (day.Distribution ?? [])
                .Where(slice => slice.Tokens > 0)
                .Select(slice => new { day.Date, slice.Model, slice.Tokens }))
            .Where(usage => UsageCredits.NormalizeModel(usage.Model) is not ("unknown" or "legacy-unattributed"))
            .ToArray();
        var missing = usages.Where(usage => UsageCredits.FindRate(
            usage.Model, usage.Date, settings.CustomModelRates, settings.IsRateCatalogPinned) is null).ToArray();
        if (missing.Length == 0)
        {
            return;
        }

        var coverage = Math.Clamp((usages.Sum(usage => usage.Tokens) - missing.Sum(usage => usage.Tokens))
            * 100d / usages.Sum(usage => usage.Tokens), 0d, 100d);
        if (coverage >= settings.MinimumRateCoverageAlertPercent)
        {
            return;
        }

        var key = FormattableString.Invariant(
            $"rate-coverage:{snapshot.Runtime}:{now:yyyy-MM}:{Math.Floor(coverage / 5d) * 5d:0}");
        if (!_monthlyKeys.Add(key))
        {
            return;
        }

        var models = missing.Select(usage => usage.Model.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order().ToArray();
        var modelNames = string.Join("、", models.Take(3));
        if (models.Length > 3) modelNames += $"等 {models.Length} 个模型";
        notifications.Add(Create(
            "rate-coverage",
            key,
            "codexU 费率覆盖提醒",
            $"{snapshot.Runtime} 本月已识别模型的用量中，{coverage:N1}% 有适用费率。请在设置中补充 {modelNames} 在用量发生日期的费率。"));
    }

    private string? ObserveWindowIdentity(
        AgentRuntime runtime,
        string label,
        RateLimitWindow? quota)
    {
        return quota is null
            ? null
            : _windowIdentities.ObserveWindow(runtime, label, quota, _clock());
    }

    private static QuotaNotification Create(
        string kind,
        string logicalKey,
        string title,
        string body)
    {
        var material = Encoding.UTF8.GetBytes($"{kind}|{logicalKey}");
        var hash = SHA256.HashData(material);
        var fingerprint = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        return new QuotaNotification($"{kind}:{fingerprint}", title, body);
    }

    private static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
        ? $"{span.TotalHours:0.#} 小时"
        : $"{Math.Max(1, Math.Round(span.TotalMinutes))} 分钟";
}
