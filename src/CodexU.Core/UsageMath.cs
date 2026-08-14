namespace CodexU.Core;

/// <summary>Which vendor's price list a model is drawn from.</summary>
public enum ModelLineage
{
    Unknown,
    OpenAi,
    Anthropic,
}

public sealed record ModelCreditRate(
    string Model,
    double InputCreditsPerMillion,
    double CachedInputCreditsPerMillion,
    double OutputCreditsPerMillion,
    DateOnly? EffectiveFrom = null,
    string? Source = null,
    string? CatalogVersion = null,
    string MatchMode = "exact");

public sealed record ModelTokenUsage(string Model, TokenBreakdown Tokens);

public sealed record DatedModelTokenUsage(DateOnly Date, string Model, TokenBreakdown Tokens);

public sealed record RateCatalogInfo(
    int SchemaVersion,
    string CatalogVersion,
    string Source,
    DateOnly PublishedOn,
    int RateCount);

public sealed record RateCatalogDocument(
    int SchemaVersion,
    string CatalogVersion,
    string Source,
    DateTimeOffset ExportedAt,
    IReadOnlyList<ModelCreditRate> Rates,
    string? BaseCatalogVersion = null);

public sealed record RateCatalogSnapshot(
    RateCatalogInfo BuiltIn,
    IReadOnlyList<ModelCreditRate> BuiltInRates);

public sealed record ModelCreditUsage(
    string Model,
    TokenBreakdown Tokens,
    double InputCredits,
    double CachedInputCredits,
    double OutputCredits,
    double CachedSavingsCredits,
    IReadOnlyList<RateCreditUsage> RateVersions,
    double CacheWriteCredits = 0)
{
    public double TotalCredits => InputCredits + CachedInputCredits + CacheWriteCredits + OutputCredits;
}

public sealed record RateCreditUsage(
    string CatalogVersion,
    string Source,
    DateOnly? EffectiveFrom,
    TokenBreakdown Tokens,
    double InputCredits,
    double CachedInputCredits,
    double OutputCredits,
    double CachedSavingsCredits,
    double CacheWriteCredits = 0)
{
    public double TotalCredits => InputCredits + CachedInputCredits + CacheWriteCredits + OutputCredits;
}

public sealed record CreditCalculation(
    double CreditsUsed,
    long UnratedTokens,
    IReadOnlyList<ModelCreditUsage> ByModel);

public static class UsageCredits
{
    public const int RateCatalogSchemaVersion = 1;
    public const int MaximumCustomRateCount = 200;
    public const int MaximumCatalogRateCount = 1_000;
    public const string CustomCatalogVersion = "custom";
    public const string CustomCatalogSource = "codexU 用户自定义";
    private const string Catalog2026071Version = "2026.07.1";
    private const string Catalog2026071Source = "用户提供的 OpenAI Credits 参考表";
    // Shown in the settings page for the built-in catalog as a whole. The per-row
    // Source still names each vendor's own table; this only has to stop claiming the
    // combined catalog is OpenAI-only now that it spans more than one lineage.
    private const string BuiltInCatalogSource = "内置费率目录（OpenAI Credits 参考表 + Anthropic 公布价目）";
    private const string CatalogAnthropic2026071Version = "anthropic-2026.07.1";
    private const string CatalogAnthropic2026071Source = "Anthropic 公布的 Claude API 价目";
    private const string CatalogAnthropic2026091Version = "anthropic-2026.09.1";
    private const string CatalogAnthropic2026091Source = "Anthropic 公布的 Claude API 价目（Sonnet 5 首发优惠到期）";
    public const string CurrentCatalogVersion = Catalog2026071Version;
    public const string CurrentCatalogSource = Catalog2026071Source;

    // Cache writes are priced as a fixed multiple of the model's base input rate
    // rather than quoted per model, so they live here instead of on ModelCreditRate.
    // Keeping them catalog-level means historical rows stay valid unchanged.
    public const double CacheWrite5mMultiplier = 1.25d;
    public const double CacheWrite1hMultiplier = 2d;

    public const double CreditsPerDollar = 25d;
    public const double DefaultAmountPerThousandCredits = 40d;
    public const double CreditsPerTwentyDollars = 500d;
    public const double CreditsPerFortyDollars = 1_000d;
    public const double CreditsPerEightyDollars = 2_000d;

    public static double ToAmount(double credits, double amountPerThousandCredits = DefaultAmountPerThousandCredits)
    {
        var safeCredits = double.IsFinite(credits) ? Math.Max(0, credits) : 0;
        var safeRate = double.IsFinite(amountPerThousandCredits) && amountPerThousandCredits > 0
            ? amountPerThousandCredits
            : DefaultAmountPerThousandCredits;
        return safeCredits / 1_000d * safeRate;
    }

    // Built-in rows form an append-only history. Never replace a published row when
    // prices change; append a row with a new version and EffectiveFrom date instead.
    private static readonly IReadOnlyList<ModelCreditRate> Rates =
    [
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.6-sol", 125d, 12.5d, 750d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.6-terra", 62.5d, 6.25d, 375d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.6-luna", 25d, 2.5d, 150d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.5-cyber", 500d, 50d, 3_000d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.5", 125d, 12.5d, 750d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.4-mini", 18.75d, 1.875d, 113d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.4", 62.5d, 6.25d, 375d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.3-codex", 43.75d, 4.375d, 350d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-5.2", 43.75d, 4.375d, 350d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-image-2.0-image", 200d, 50d, 750d),
        BuiltIn(Catalog2026071Version, Catalog2026071Source, null, "gpt-image-2.0-text", 125d, 31.25d, 250d),

        // Anthropic list prices converted at CreditsPerDollar. Cached input is the
        // published 0.1x cache-read multiple, matching the OpenAI rows above.
        // Only the alias is registered: NormalizeModel collapses a dated snapshot
        // suffix onto it, so claude-haiku-4-5-20251001 resolves through this row.
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-fable-5", 250d, 25d, 1_250d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-mythos-5", 250d, 25d, 1_250d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-opus-5", 125d, 12.5d, 625d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-opus-4-8", 125d, 12.5d, 625d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-opus-4-7", 125d, 12.5d, 625d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-opus-4-6", 125d, 12.5d, 625d),
        // Sonnet 5 launched on introductory pricing; the standard rate takes over the
        // day after it lapses. Usage is replayed against the row effective on its own
        // date, so historical months keep billing at the introductory rate.
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-sonnet-5", 50d, 5d, 250d),
        BuiltIn(CatalogAnthropic2026091Version, CatalogAnthropic2026091Source, new DateOnly(2026, 9, 1), "claude-sonnet-5", 75d, 7.5d, 375d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-sonnet-4-6", 75d, 7.5d, 375d),
        BuiltIn(CatalogAnthropic2026071Version, CatalogAnthropic2026071Source, null, "claude-haiku-4-5", 25d, 2.5d, 125d)
    ];

    public static IReadOnlyList<ModelCreditRate> BuiltInRates => Rates;

    public static bool IsBuiltInRate(ModelCreditRate candidate) => Rates.Any(rate =>
        MatchesBuiltInPricing(rate, candidate)
        && string.Equals(rate.Source, candidate.Source, StringComparison.Ordinal)
        && string.Equals(rate.CatalogVersion, candidate.CatalogVersion, StringComparison.Ordinal));

    /// <summary>
    /// Finds the built-in row a candidate is priced identically to, ignoring
    /// provenance. Import uses this to give a row that lost its Source and
    /// CatalogVersion its own identity back, rather than stamping it with the
    /// enclosing document's — which is only ever right for rows from the same
    /// catalog lineage, and the built-in catalog carries more than one.
    /// </summary>
    public static ModelCreditRate? FindBuiltInByPricing(ModelCreditRate candidate) =>
        Rates.FirstOrDefault(rate => MatchesBuiltInPricing(rate, candidate));

    private static bool MatchesBuiltInPricing(ModelCreditRate rate, ModelCreditRate candidate) =>
        string.Equals(NormalizeModel(rate.Model), NormalizeModel(candidate.Model), StringComparison.Ordinal)
        && rate.EffectiveFrom == candidate.EffectiveFrom
        && string.Equals(rate.MatchMode, candidate.MatchMode, StringComparison.OrdinalIgnoreCase)
        && rate.InputCreditsPerMillion.Equals(candidate.InputCreditsPerMillion)
        && rate.CachedInputCreditsPerMillion.Equals(candidate.CachedInputCreditsPerMillion)
        && rate.OutputCreditsPerMillion.Equals(candidate.OutputCreditsPerMillion);

    public static RateCatalogInfo BuiltInCatalog => new(
        RateCatalogSchemaVersion,
        CurrentCatalogVersion,
        BuiltInCatalogSource,
        new DateOnly(2026, 7, 14),
        Rates.Count);

    public static RateCatalogSnapshot CatalogSnapshot => new(BuiltInCatalog, Rates);

    public static CreditCalculation Calculate(
        IEnumerable<ModelTokenUsage> usages,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false) =>
        Calculate(
            usages.Select(usage => new DatedModelTokenUsage(
                DateOnly.FromDateTime(DateTime.Today),
                usage.Model,
                usage.Tokens)),
            customRates,
            completeRateCatalog);

    public static CreditCalculation Calculate(
        IEnumerable<DatedModelTokenUsage> usages,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false)
    {
        var ratedModels = new Dictionary<string, RatedModelAccumulator>(StringComparer.Ordinal);
        long unratedTokens = 0;

        foreach (var usage in usages)
        {
            var rate = FindRate(usage.Model, usage.Date, customRates, completeRateCatalog);
            if (rate is null)
            {
                unratedTokens += usage.Tokens.VisibleTotalTokens;
                continue;
            }

            var ratedModel = NormalizeModel(usage.Model);
            if (!ratedModels.TryGetValue(ratedModel, out var accumulator))
            {
                accumulator = new RatedModelAccumulator(ratedModel);
                ratedModels.Add(ratedModel, accumulator);
            }

            accumulator.Add(usage.Tokens, rate);
        }

        var rated = ratedModels.Values
            .Select(accumulator => accumulator.ToUsage())
            .ToArray();

        return new CreditCalculation(
            rated.Sum(item => item.TotalCredits),
            unratedTokens,
            rated.OrderByDescending(item => item.TotalCredits).ToArray());
    }

    public static ModelCreditRate? FindRate(
        string? model,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false) =>
        FindRate(model, DateOnly.FromDateTime(DateTime.Today), customRates, completeRateCatalog);

    public static ModelCreditRate? FindRate(
        string? model,
        DateOnly usageDate,
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeRateCatalog = false)
    {
        var normalized = NormalizeModel(model);
        var custom = FindMatchingRate(normalized, usageDate, customRates, allowPrefix: true);
        if (custom is not null)
        {
            return custom;
        }

        // A pinned catalog suppresses the built-in fallback only for the vendors it
        // actually prices. Suppressing it outright meant importing an OpenAI-only
        // archive — the obvious thing to do when reproducing a historical ChatGPT bill,
        // and import pins unconditionally — left every claude-* model unrated: US$0.00
        // beside a real token count, 0% coverage, and a tray alert asking the user to
        // supply rates they never meant to manage. A snapshot that says nothing about a
        // vendor was never claiming to describe it.
        if (completeRateCatalog && CoversLineageOf(normalized, customRates))
        {
            return null;
        }

        // A research-preview carve-out that belongs to one vendor's lineage. The test
        // was an unanchored substring applied to every model from both runtimes, so any
        // future Anthropic id containing the word would have dropped silently into
        // unrated tokens.
        if (LineageOf(normalized) == ModelLineage.OpenAi
            && normalized.Contains("spark", StringComparison.Ordinal))
        {
            return null;
        }

        return FindMatchingRate(normalized, usageDate, Rates, allowPrefix: false);
    }

    /// <summary>
    /// Which vendor's price list a model belongs to. Derived from the id rather than
    /// stored on the rate: ids are already vendor-namespaced, so a tag would be a
    /// second source of truth to keep in step — and every existing pinned catalog and
    /// exported document would need migrating to carry it.
    /// </summary>
    public static ModelLineage LineageOf(string? model)
    {
        var normalized = NormalizeModel(model);
        if (normalized.StartsWith("claude-", StringComparison.Ordinal))
        {
            return ModelLineage.Anthropic;
        }

        return normalized.StartsWith("gpt-", StringComparison.Ordinal)
            || normalized.StartsWith("o1", StringComparison.Ordinal)
            || normalized.StartsWith("o3", StringComparison.Ordinal)
            || normalized.StartsWith("o4", StringComparison.Ordinal)
            || normalized.StartsWith("codex", StringComparison.Ordinal)
                ? ModelLineage.OpenAi
                : ModelLineage.Unknown;
    }

    /// <summary>
    /// Whether a pinned catalog means to cover this model.
    ///
    /// The relaxation is deliberately narrow: built-ins come back only when there is
    /// positive evidence the snapshot is vendor-specific and this vendor is not in it.
    /// A catalog whose model names cannot be classified — a private deployment, a
    /// naming scheme this code has not seen — keeps suppressing built-ins exactly as
    /// before, because silently re-pricing a pinned snapshot would be a worse failure
    /// than the one being fixed: it changes numbers the user pinned precisely so they
    /// would not change.
    /// </summary>
    private static bool CoversLineageOf(string normalized, IReadOnlyList<ModelCreditRate>? pinnedRates)
    {
        var lineage = LineageOf(normalized);
        if (lineage == ModelLineage.Unknown)
        {
            return true;
        }

        var pinnedLineages = pinnedRates?
            .Where(rate => rate is not null)
            .Select(rate => LineageOf(rate.Model))
            .Where(pinned => pinned != ModelLineage.Unknown)
            .ToHashSet() ?? [];

        return pinnedLineages.Count == 0 || pinnedLineages.Contains(lineage);
    }

    private static ModelCreditRate? FindMatchingRate(
        string normalized,
        DateOnly usageDate,
        IReadOnlyList<ModelCreditRate>? rates,
        bool allowPrefix) => rates?
        .Where(rate => rate.EffectiveFrom is null || rate.EffectiveFrom <= usageDate)
        .OrderByDescending(rate => NormalizeModel(rate.Model).Length)
        .ThenByDescending(rate => rate.EffectiveFrom ?? DateOnly.MinValue)
        .FirstOrDefault(rate =>
        {
            var rateModel = NormalizeModel(rate.Model);
            return string.Equals(normalized, rateModel, StringComparison.Ordinal)
                || allowPrefix
                    && string.Equals(rate.MatchMode, "prefix", StringComparison.OrdinalIgnoreCase)
                    && normalized.StartsWith(rateModel + "-", StringComparison.Ordinal);
        });

    public static RateCatalogDocument CreateCatalogDocument(
        IReadOnlyList<ModelCreditRate>? customRates = null,
        bool completeSnapshot = false,
        string? catalogVersion = null,
        string? source = null,
        string? baseCatalogVersion = null)
    {
        var resolvedRates = completeSnapshot
            ? new Dictionary<(string Model, DateOnly? EffectiveFrom), ModelCreditRate>(
                ModelRateVersionKeyComparer.Instance)
            : Rates.ToDictionary(
                rate => (NormalizeModel(rate.Model), rate.EffectiveFrom),
                ModelRateVersionKeyComparer.Instance);
        var customKeys = new HashSet<(string Model, DateOnly? EffectiveFrom)>(ModelRateVersionKeyComparer.Instance);
        var hasCustomRates = false;
        foreach (var customRate in customRates ?? [])
        {
            if (customRate is null)
            {
                throw new ArgumentException("费率目录不能包含 null 项。", nameof(customRates));
            }

            var normalizedModel = NormalizeModel(customRate.Model);
            var key = (normalizedModel, customRate.EffectiveFrom);
            if (!customKeys.Add(key))
            {
                throw new ArgumentException(
                    $"模型 {customRate.Model} 在同一生效日期存在重复费率。",
                    nameof(customRates));
            }

            var normalizedRate = customRate with
            {
                Model = normalizedModel,
                Source = customRate.Source?.Trim(),
                CatalogVersion = customRate.CatalogVersion?.Trim(),
                MatchMode = string.Equals(customRate.MatchMode, "prefix", StringComparison.OrdinalIgnoreCase)
                    ? "prefix"
                    : "exact"
            };
            if (completeSnapshot)
            {
                normalizedRate = normalizedRate with
                {
                    Source = string.IsNullOrWhiteSpace(normalizedRate.Source)
                        ? source?.Trim()
                        : normalizedRate.Source,
                    CatalogVersion = string.IsNullOrWhiteSpace(normalizedRate.CatalogVersion)
                        ? catalogVersion?.Trim()
                        : normalizedRate.CatalogVersion
                };
            }
            else if (!IsBuiltInRate(normalizedRate))
            {
                hasCustomRates = true;
                normalizedRate = normalizedRate with
                {
                    Source = string.IsNullOrWhiteSpace(normalizedRate.Source)
                        ? CustomCatalogSource
                        : normalizedRate.Source,
                    CatalogVersion = string.IsNullOrWhiteSpace(normalizedRate.CatalogVersion)
                        ? CustomCatalogVersion
                        : normalizedRate.CatalogVersion
                };
            }

            resolvedRates[key] = normalizedRate;
        }

        // Seeded built-in rows for a model the user priced themselves must not
        // outlive that decision. Built-ins now span several effective dates, so a
        // user's undated row would otherwise be silently superseded from the built-in
        // row's start date onward — their own price quietly stops applying on a date
        // they never chose. Only rows they did not supply are dropped.
        var customizedModels = customKeys
            .Select(key => key.Model)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in resolvedRates.Keys
            .Where(key => !customKeys.Contains(key) && customizedModels.Contains(key.Item1))
            .ToArray())
        {
            resolvedRates.Remove(stale);
        }

        var rates = resolvedRates.Values
            .OrderBy(rate => NormalizeModel(rate.Model), StringComparer.Ordinal)
            .ThenBy(rate => rate.EffectiveFrom ?? DateOnly.MinValue)
            .ToArray();
        if (completeSnapshot && rates.Length == 0)
        {
            throw new ArgumentException("完整费率目录至少需要一条费率。", nameof(customRates));
        }

        var documentVersion = completeSnapshot
            ? RequireCatalogLabel(catalogVersion, 40, nameof(catalogVersion))
            : hasCustomRates ? CustomCatalogVersion : CurrentCatalogVersion;
        var documentSource = completeSnapshot
            ? RequireCatalogLabel(source, 200, nameof(source))
            : hasCustomRates ? CustomCatalogSource : CurrentCatalogSource;
        return new RateCatalogDocument(
            RateCatalogSchemaVersion,
            documentVersion,
            documentSource,
            DateTimeOffset.Now,
            rates,
            completeSnapshot ? NormalizeOptionalCatalogLabel(baseCatalogVersion, 40) : CurrentCatalogVersion);
    }

    private static string RequireCatalogLabel(string? value, int maximumLength, string parameterName)
    {
        var normalized = NormalizeOptionalCatalogLabel(value, maximumLength);
        return normalized ?? throw new ArgumentException("完整费率目录的版本和来源不能为空。", parameterName);
    }

    private static string? NormalizeOptionalCatalogLabel(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentException($"费率目录元数据不能超过 {maximumLength} 个字符。", nameof(value));
        }

        return trimmed;
    }

    public static string NormalizeModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "unknown";
        }

        var normalized = model.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        if (normalized.EndsWith("-latest", StringComparison.Ordinal))
        {
            normalized = normalized[..^"-latest".Length];
        }

        // Vendors ship an alias and a dated snapshot that price identically —
        // claude-haiku-4-5 and claude-haiku-4-5-20251001 are the same model. Built-in
        // rows are matched exactly, so without collapsing the suffix every snapshot id
        // would need its own row and any id released later would silently go unrated.
        normalized = StripDatedSnapshotSuffix(normalized);

        return normalized switch
        {
            "gpt-5.2-codex" => "gpt-5.2",
            "gpt-5.3-codex-spark" => "gpt-5.3-codex-spark",
            _ => normalized
        };
    }

    /// <summary>
    /// Drops a trailing <c>-YYYYMMDD</c> snapshot suffix. Only a plausible calendar
    /// date is removed, so an id that merely ends in eight digits keeps them.
    /// </summary>
    private static string StripDatedSnapshotSuffix(string normalized)
    {
        const int SuffixLength = 9; // "-" plus eight digits.
        if (normalized.Length <= SuffixLength || normalized[^SuffixLength] != '-')
        {
            return normalized;
        }

        var digits = normalized.AsSpan(normalized.Length - 8);
        foreach (var character in digits)
        {
            if (!char.IsAsciiDigit(character))
            {
                return normalized;
            }
        }

        var year = int.Parse(digits[..4]);
        var month = int.Parse(digits.Slice(4, 2));
        var day = int.Parse(digits.Slice(6, 2));
        return year >= 2000 && month is >= 1 and <= 12 && day is >= 1 and <= 31
            ? normalized[..^SuffixLength]
            : normalized;
    }

    public static DateTimeOffset? FromUnixTime(long? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        try
        {
            return value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value)
                : DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ModelCreditRate BuiltIn(
        string catalogVersion,
        string source,
        DateOnly? effectiveFrom,
        string model,
        double input,
        double cachedInput,
        double output) =>
        new(model, input, cachedInput, output, effectiveFrom, source, catalogVersion, "exact");

    // Cache writes bill at a multiple of the model's base input rate. A source that
    // does not report the 5m/1h split leaves both slices at zero, so this adds
    // nothing and the tokens stay priced as plain input.
    private static double CacheWriteCreditsFor(TokenBreakdown tokens, ModelCreditRate rate) =>
        tokens.BillableCacheWrite5mTokens / 1_000_000d * rate.InputCreditsPerMillion * CacheWrite5mMultiplier
        + tokens.BillableCacheWrite1hTokens / 1_000_000d * rate.InputCreditsPerMillion * CacheWrite1hMultiplier;

    private sealed class RatedModelAccumulator(string model)
    {
        private TokenBreakdown _tokens = TokenBreakdown.Zero;
        private double _inputCredits;
        private double _cachedInputCredits;
        private double _cacheWriteCredits;
        private double _outputCredits;
        private double _cachedSavingsCredits;
        private readonly Dictionary<RateVersionKey, RateVersionAccumulator> _rateVersions = [];

        public void Add(TokenBreakdown tokens, ModelCreditRate rate)
        {
            _tokens = _tokens.Add(tokens);
            _inputCredits += tokens.UncachedInputTokens / 1_000_000d * rate.InputCreditsPerMillion;
            _cachedInputCredits += tokens.BillableCachedInputTokens / 1_000_000d * rate.CachedInputCreditsPerMillion;
            _cacheWriteCredits += CacheWriteCreditsFor(tokens, rate);
            _outputCredits += Math.Max(0, tokens.OutputTokens) / 1_000_000d * rate.OutputCreditsPerMillion;
            _cachedSavingsCredits += tokens.BillableCachedInputTokens / 1_000_000d
                * Math.Max(0, rate.InputCreditsPerMillion - rate.CachedInputCreditsPerMillion);

            var key = new RateVersionKey(
                rate.CatalogVersion ?? "未标注版本",
                rate.Source ?? "未标注来源",
                rate.EffectiveFrom);
            if (!_rateVersions.TryGetValue(key, out var rateVersion))
            {
                rateVersion = new RateVersionAccumulator(key);
                _rateVersions.Add(key, rateVersion);
            }

            rateVersion.Add(tokens, rate);
        }

        public ModelCreditUsage ToUsage() => new(
            model,
            _tokens,
            _inputCredits,
            _cachedInputCredits,
            _outputCredits,
            _cachedSavingsCredits,
            _rateVersions.Values
                .OrderBy(item => item.Key.EffectiveFrom ?? DateOnly.MinValue)
                .ThenBy(item => item.Key.CatalogVersion, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.ToUsage())
                .ToArray(),
            _cacheWriteCredits);
    }

    private readonly record struct RateVersionKey(
        string CatalogVersion,
        string Source,
        DateOnly? EffectiveFrom);

    private sealed class RateVersionAccumulator(RateVersionKey key)
    {
        private TokenBreakdown _tokens = TokenBreakdown.Zero;
        private double _inputCredits;
        private double _cachedInputCredits;
        private double _cacheWriteCredits;
        private double _outputCredits;
        private double _cachedSavingsCredits;

        public RateVersionKey Key => key;

        public void Add(TokenBreakdown tokens, ModelCreditRate rate)
        {
            _tokens = _tokens.Add(tokens);
            _inputCredits += tokens.UncachedInputTokens / 1_000_000d * rate.InputCreditsPerMillion;
            _cachedInputCredits += tokens.BillableCachedInputTokens / 1_000_000d * rate.CachedInputCreditsPerMillion;
            _cacheWriteCredits += CacheWriteCreditsFor(tokens, rate);
            _outputCredits += Math.Max(0, tokens.OutputTokens) / 1_000_000d * rate.OutputCreditsPerMillion;
            _cachedSavingsCredits += tokens.BillableCachedInputTokens / 1_000_000d
                * Math.Max(0, rate.InputCreditsPerMillion - rate.CachedInputCreditsPerMillion);
        }

        public RateCreditUsage ToUsage() => new(
            key.CatalogVersion,
            key.Source,
            key.EffectiveFrom,
            _tokens,
            _inputCredits,
            _cachedInputCredits,
            _outputCredits,
            _cachedSavingsCredits,
            _cacheWriteCredits);
    }
}

public static class SubscriptionPricing
{
    public static double? InferMonthlyAmount(
        string? planType,
        AgentRuntime runtime = AgentRuntime.Codex)
    {
        if (string.IsNullOrWhiteSpace(planType))
        {
            return null;
        }

        var normalized = planType.Trim().ToLowerInvariant()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        // The same word means different money per vendor, so never fall through from
        // one table to the other. An unknown plan returns null and the UI uses the
        // manual amount from settings — a wrong number is worse than an absent one,
        // because it silently corrupts the payback multiple.
        return runtime == AgentRuntime.ClaudeCode
            ? InferClaudeAmount(normalized)
            : InferChatGptAmount(normalized);
    }

    private static double? InferChatGptAmount(string normalized) => normalized switch
    {
        "free" => 0d,
        "plus" => 20d,
        "prolite" or "pro100" => 100d,
        "pro" or "pro200" => 200d,
        _ => null
    };

    private static double? InferClaudeAmount(string normalized) => normalized switch
    {
        "free" => 0d,
        "pro" => 20d,
        "max" or "max5x" or "max5" or "max100" => 100d,
        "max20x" or "max20" or "max200" => 200d,
        // Team and Enterprise are per-seat and negotiated; guessing a seat price
        // would be inventing the user's bill.
        _ => null
    };
}
