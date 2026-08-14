namespace CodexU.Core;

public sealed record AppSettings(
    string? CodexHome = null,
    string? CodexExecutable = null,
    string? DefaultWorkspace = null,
    string Theme = "dark",
    bool ShowSubagents = false,
    bool CompactMode = false,
    bool StatusStripEnabled = false,
    bool StatusStripPositionLocked = false,
    bool DesktopMode = false,
    bool CloseToTray = true,
    bool StartAtLogin = false,
    bool NotificationsEnabled = true,
    bool QuotaForecastAlertsEnabled = true,
    int FiveHourAlertPercent = 20,
    int SevenDayAlertPercent = 20,
    int AutoRefreshMinutes = 5,
    bool IncrementalIndexEnabled = true,
    int UiScalePercent = 110,
    double AmountPerThousandCredits = UsageCredits.DefaultAmountPerThousandCredits,
    string CreditCurrencySymbol = "US$",
    // Manual fallbacks used only when a runtime's plan cannot be priced automatically.
    // They are per runtime because the same money question has a different answer per
    // vendor: one shared field defaulting to a ChatGPT price reported US$200 for a
    // Claude user whenever the statusline snapshot was missing. The defaults are each
    // vendor's entry paid tier — ChatGPT Pro and Claude Pro.
    double CodexMonthlySubscriptionAmount = 200d,
    double ClaudeMonthlySubscriptionAmount = 20d,
    // The flag that chooses between auto-detection and the manual value is per runtime
    // for the same reason the amounts are. Sharing it means typing into one vendor's
    // amount box switches the OTHER vendor off auto-detection too, so a reliably
    // detected price is abandoned for a stale manual one the user never touched.
    bool CodexAutoDetectSubscriptionAmount = true,
    bool ClaudeAutoDetectSubscriptionAmount = true,
    bool CheckForUpdates = true,
    bool IncludePrereleaseUpdates = false,
    double MonthlyAmountAlert = 0d,
    double MinimumRateCoverageAlertPercent = 80d,
    string GlobalHotKey = "Ctrl+U",
    string StatusStripQuotaMode = "remaining",
    bool StatusStripShowTodayTokens = true,
    IReadOnlyList<ModelCreditRate>? CustomModelRates = null,
    bool IsRateCatalogPinned = false,
    string? PinnedRateCatalogVersion = null,
    string? PinnedRateCatalogSource = null,
    string? PinnedRateCatalogBaseVersion = null)
{
    private static readonly char[] InvalidWindowsPathCharacters = ['\0', '"', '<', '>', '|', '?', '*'];

    public AppSettings Validate()
    {
        ValidatePath(CodexHome, nameof(CodexHome));
        ValidatePath(CodexExecutable, nameof(CodexExecutable));
        ValidatePath(DefaultWorkspace, nameof(DefaultWorkspace));
        ValidateCustomRates(CustomModelRates, IsRateCatalogPinned);
        ValidatePinnedRateCatalog();
        return this;
    }

    public AppSettings Normalize() => this with
    {
        CodexHome = NormalizePath(CodexHome),
        CodexExecutable = NormalizePath(CodexExecutable),
        DefaultWorkspace = NormalizePath(DefaultWorkspace),
        Theme = Theme?.ToLowerInvariant() is "light" or "system" ? Theme.ToLowerInvariant() : "dark",
        FiveHourAlertPercent = Math.Clamp(FiveHourAlertPercent, 1, 99),
        SevenDayAlertPercent = Math.Clamp(SevenDayAlertPercent, 1, 99),
        AutoRefreshMinutes = Math.Clamp(AutoRefreshMinutes, 1, 60),
        UiScalePercent = UiScalePercent == 0 ? 110 : Math.Clamp(UiScalePercent, 90, 140),
        AmountPerThousandCredits = !double.IsFinite(AmountPerThousandCredits) || AmountPerThousandCredits <= 0
            ? UsageCredits.DefaultAmountPerThousandCredits
            : Math.Clamp(AmountPerThousandCredits, 0.01d, 1_000_000d),
        // All equivalent-value and subscription comparisons use one currency basis.
        // Keep the legacy JSON field for settings-file compatibility, but never accept
        // a second display currency without an explicit exchange-rate model.
        CreditCurrencySymbol = "US$",
        CodexMonthlySubscriptionAmount = NormalizeSubscriptionAmount(CodexMonthlySubscriptionAmount, 200d),
        ClaudeMonthlySubscriptionAmount = NormalizeSubscriptionAmount(ClaudeMonthlySubscriptionAmount, 20d),
        MonthlyAmountAlert = !double.IsFinite(MonthlyAmountAlert) || MonthlyAmountAlert < 0
            ? 0d
            : Math.Clamp(MonthlyAmountAlert, 0d, 1_000_000_000d),
        MinimumRateCoverageAlertPercent = !double.IsFinite(MinimumRateCoverageAlertPercent)
            ? 80d
            : Math.Clamp(MinimumRateCoverageAlertPercent, 0d, 100d),
        GlobalHotKey = HotKeyGesture.Normalize(GlobalHotKey),
        StatusStripQuotaMode = string.Equals(StatusStripQuotaMode, "used", StringComparison.OrdinalIgnoreCase)
            ? "used"
            : "remaining",
        CustomModelRates = NormalizeCustomRates(CustomModelRates, HasUsablePinnedRateCatalog()),
        IsRateCatalogPinned = HasUsablePinnedRateCatalog(),
        PinnedRateCatalogVersion = HasUsablePinnedRateCatalog()
            ? NormalizeLabel(PinnedRateCatalogVersion, 40)
            : null,
        PinnedRateCatalogSource = HasUsablePinnedRateCatalog()
            ? NormalizeLabel(PinnedRateCatalogSource, 200)
            : null,
        PinnedRateCatalogBaseVersion = HasUsablePinnedRateCatalog()
            ? NormalizeLabel(PinnedRateCatalogBaseVersion, 40)
            : null
    };

    /// <summary>
    /// Picks the manual fallback price for one runtime. Kept here so the choice cannot
    /// drift between the dashboard, the settings screen and the tray.
    /// </summary>
    public double ManualSubscriptionAmountFor(AgentRuntime runtime) => runtime == AgentRuntime.ClaudeCode
        ? ClaudeMonthlySubscriptionAmount
        : CodexMonthlySubscriptionAmount;

    public bool AutoDetectSubscriptionAmountFor(AgentRuntime runtime) => runtime == AgentRuntime.ClaudeCode
        ? ClaudeAutoDetectSubscriptionAmount
        : CodexAutoDetectSubscriptionAmount;

    private static double NormalizeSubscriptionAmount(double amount, double fallback) =>
        !double.IsFinite(amount) || amount < 0
            ? fallback
            : Math.Clamp(amount, 0d, 1_000_000d);

    private bool HasUsablePinnedRateCatalog() =>
        IsRateCatalogPinned
        && CustomModelRates?.Any(rate => rate is not null && !string.IsNullOrWhiteSpace(rate.Model)) == true
        && !string.IsNullOrWhiteSpace(PinnedRateCatalogVersion)
        && !string.IsNullOrWhiteSpace(PinnedRateCatalogSource);

    private static IReadOnlyList<ModelCreditRate> NormalizeCustomRates(
        IReadOnlyList<ModelCreditRate>? rates,
        bool completeCatalog)
    {
        if (rates is null || rates.Count == 0)
        {
            return [];
        }

        var normalized = rates
            .Where(rate => rate is not null && !string.IsNullOrWhiteSpace(rate.Model))
            .Select(NormalizeCustomRate)
            .GroupBy(rate => (rate.Model, rate.EffectiveFrom), ModelRateVersionKeyComparer.Instance)
            .Select(group => group.Last())
            .ToArray();

        var ordered = normalized
            .Where(UsageCredits.IsBuiltInRate)
            .Concat(normalized
                .Where(rate => !UsageCredits.IsBuiltInRate(rate))
                .Take(completeCatalog
                    ? UsageCredits.MaximumCatalogRateCount
                    : UsageCredits.MaximumCustomRateCount))
            .OrderBy(rate => rate.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rate => rate.EffectiveFrom ?? DateOnly.MinValue)
            .ToArray();
        return completeCatalog
            ? ordered.Take(UsageCredits.MaximumCatalogRateCount).ToArray()
            : ordered;
    }

    private static void ValidateCustomRates(
        IReadOnlyList<ModelCreditRate>? rates,
        bool completeCatalog)
    {
        if (rates is null)
        {
            return;
        }

        if (rates.Count > UsageCredits.MaximumCatalogRateCount)
        {
            throw new ArgumentException(
                $"费率目录不能超过 {UsageCredits.MaximumCatalogRateCount} 条。",
                nameof(CustomModelRates));
        }

        var keys = new HashSet<(string Model, DateOnly? EffectiveFrom)>(ModelRateVersionKeyComparer.Instance);
        var customRateCount = 0;
        foreach (var rate in rates)
        {
            if (rate is null)
            {
                throw new ArgumentException("费率目录不能包含 null 项。", nameof(CustomModelRates));
            }

            if (string.IsNullOrWhiteSpace(rate.Model) || rate.Model.Trim().Length > 100)
            {
                throw new ArgumentException("模型名称不能为空且不能超过 100 个字符。", nameof(CustomModelRates));
            }

            ValidateRate(rate.InputCreditsPerMillion, "普通输入");
            ValidateRate(rate.CachedInputCreditsPerMillion, "缓存输入");
            ValidateRate(rate.OutputCreditsPerMillion, "输出");
            if (!string.Equals(rate.MatchMode, "exact", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rate.MatchMode, "prefix", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("费率匹配方式只能是 exact 或 prefix。", nameof(CustomModelRates));
            }

            var normalized = NormalizeCustomRate(rate);
            if (!keys.Add((normalized.Model, normalized.EffectiveFrom)))
            {
                throw new ArgumentException(
                    $"模型 {rate.Model} 在同一生效日期存在重复费率。",
                    nameof(CustomModelRates));
            }

            if (!completeCatalog
                && !UsageCredits.IsBuiltInRate(normalized)
                && ++customRateCount > UsageCredits.MaximumCustomRateCount)
            {
                throw new ArgumentException(
                    $"自定义费率不能超过 {UsageCredits.MaximumCustomRateCount} 条；内置基线不计入该限制。",
                    nameof(CustomModelRates));
            }
        }
    }

    private void ValidatePinnedRateCatalog()
    {
        if (!IsRateCatalogPinned)
        {
            return;
        }

        if (CustomModelRates is not { Count: > 0 })
        {
            throw new ArgumentException("锁定的完整费率目录不能为空。", nameof(CustomModelRates));
        }

        if (string.IsNullOrWhiteSpace(PinnedRateCatalogVersion)
            || PinnedRateCatalogVersion.Trim().Length > 40)
        {
            throw new ArgumentException(
                "锁定费率目录的版本不能为空且不能超过 40 个字符。",
                nameof(PinnedRateCatalogVersion));
        }

        if (string.IsNullOrWhiteSpace(PinnedRateCatalogSource)
            || PinnedRateCatalogSource.Trim().Length > 200)
        {
            throw new ArgumentException(
                "锁定费率目录的来源不能为空且不能超过 200 个字符。",
                nameof(PinnedRateCatalogSource));
        }

        if (PinnedRateCatalogBaseVersion?.Trim().Length > 40)
        {
            throw new ArgumentException(
                "锁定费率目录的基线版本不能超过 40 个字符。",
                nameof(PinnedRateCatalogBaseVersion));
        }
    }

    private static ModelCreditRate NormalizeCustomRate(ModelCreditRate rate) => new(
        UsageCredits.NormalizeModel(rate.Model),
        NormalizeRate(rate.InputCreditsPerMillion),
        NormalizeRate(rate.CachedInputCreditsPerMillion),
        NormalizeRate(rate.OutputCreditsPerMillion),
        rate.EffectiveFrom,
        NormalizeLabel(rate.Source, 200),
        NormalizeLabel(rate.CatalogVersion, 40),
        string.Equals(rate.MatchMode, "prefix", StringComparison.OrdinalIgnoreCase)
            ? "prefix"
            : "exact");

    private static void ValidateRate(double value, string field)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1_000_000d)
        {
            throw new ArgumentException($"{field}费率必须是 0 到 1,000,000 之间的有限数值。", nameof(CustomModelRates));
        }
    }

    private static double NormalizeRate(double value) =>
        !double.IsFinite(value) || value < 0 ? 0d : Math.Clamp(value, 0d, 1_000_000d);

    private static string? NormalizeLabel(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            ValidatePath(value, nameof(value));
            return Path.GetFullPath(value.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void ValidatePath(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            var trimmed = value.Trim();
            if (trimmed.IndexOfAny(InvalidWindowsPathCharacters) >= 0)
            {
                throw new ArgumentException("路径包含 Windows 不允许的字符。", propertyName);
            }

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex >= 0 && colonIndex != 1)
            {
                throw new ArgumentException("路径中的冒号位置无效。", propertyName);
            }

            _ = Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{propertyName} 不是有效的本地路径。", propertyName, exception);
        }
    }

}

internal sealed class ModelRateVersionKeyComparer : IEqualityComparer<(string Model, DateOnly? EffectiveFrom)>
{
    public static ModelRateVersionKeyComparer Instance { get; } = new();

    public bool Equals(
        (string Model, DateOnly? EffectiveFrom) x,
        (string Model, DateOnly? EffectiveFrom) y) =>
        string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase)
        && x.EffectiveFrom == y.EffectiveFrom;

    public int GetHashCode((string Model, DateOnly? EffectiveFrom) value) =>
        HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model), value.EffectiveFrom);
}

public static class HotKeyGesture
{
    public const string Default = "Ctrl+U";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+U",
        "Ctrl+Shift+U",
        "Ctrl+Alt+U",
        "Ctrl+Shift+C",
        "Ctrl+Alt+C"
    };

    public static string Normalize(string? value) =>
        value is not null && Allowed.Contains(value.Trim()) ? value.Trim() : Default;

    public static IReadOnlyList<string> Supported => Allowed.OrderBy(value => value).ToArray();
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    bool IsPrerelease,
    string? ReleaseName,
    string? ReleaseUrl,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CheckedAt,
    string Status,
    string? Notes = null);

public sealed record LocalOperationResult(
    bool Success,
    string Message,
    string? Path = null,
    AppSettings? Settings = null,
    IReadOnlyList<TodoItem>? Todos = null);

public sealed record StatusStripControlState(
    bool ConfiguredEnabled,
    bool Visible,
    bool PositionLocked,
    bool HasManualPosition,
    string PositionMode,
    string DisplayName,
    string Message);

public sealed record TodoItem(
    string Id,
    string Text,
    bool Done,
    string Priority,
    DateOnly? DueDate,
    string? ThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null);

public sealed record TodoMutation(
    string? Id,
    string Text,
    string Priority,
    DateOnly? DueDate,
    string? ThreadId);

public sealed record GoalItem(
    string Id,
    string Objective,
    string Status,
    long? TokenBudget,
    long TokensUsed,
    long TimeUsedSeconds,
    DateTimeOffset? UpdatedAt);

public sealed record ModelUsage(
    string Model,
    long Tokens,
    int EventCount);

public sealed record TaskLifecycleStats(
    int Started,
    int Completed,
    int Aborted,
    long DurationMilliseconds,
    long LongestDurationMilliseconds)
{
    public static TaskLifecycleStats Empty { get; } = new(0, 0, 0, 0, 0);
}

public sealed record IndexStatus(
    bool Enabled,
    int ReusedFiles,
    int IncrementalFiles,
    int ParsedFiles,
    int TotalFiles,
    DateTimeOffset? UpdatedAt);
