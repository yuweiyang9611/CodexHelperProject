using System.Text.Json;
using System.Text.Json.Serialization;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed record ImportedRateCatalog(
    string CatalogVersion,
    string Source,
    IReadOnlyList<ModelCreditRate> Rates,
    string? BaseCatalogVersion = null);

public sealed class RateCatalogFileService
{
    private const long MaximumCatalogBytes = 1024 * 1024;

    private static readonly HashSet<string> ReservedApplicationDataFileNames = new(
        [
            "settings.json",
            "settings.json.bak",
            "todos.json",
            "todos.json.bak",
            "update-check.json",
            "startup.log"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string _applicationDataDirectory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public RateCatalogFileService(string? applicationDataDirectory = null)
    {
        var configuredDirectory = string.IsNullOrWhiteSpace(applicationDataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "codexU")
            : applicationDataDirectory;
        _applicationDataDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(configuredDirectory));
    }

    public async Task<LocalOperationResult> ExportAsync(
        IReadOnlyList<ModelCreditRate>? customRates,
        string path,
        CancellationToken cancellationToken = default,
        bool completeSnapshot = false,
        string? catalogVersion = null,
        string? source = null,
        string? baseCatalogVersion = null)
    {
        var requestedPath = Path.GetFullPath(path);
        var exportPath = Path.ChangeExtension(requestedPath, ".json");
        EnsureExportTargetIsAllowed(requestedPath);
        EnsureExportTargetIsAllowed(exportPath);

        var normalizedSettings = new AppSettings(
                CustomModelRates: customRates,
                IsRateCatalogPinned: completeSnapshot,
                PinnedRateCatalogVersion: catalogVersion,
                PinnedRateCatalogSource: source,
                PinnedRateCatalogBaseVersion: baseCatalogVersion)
            .Validate()
            .Normalize();
        var document = UsageCredits.CreateCatalogDocument(
            normalizedSettings.CustomModelRates,
            completeSnapshot,
            normalizedSettings.PinnedRateCatalogVersion,
            normalizedSettings.PinnedRateCatalogSource,
            normalizedSettings.PinnedRateCatalogBaseVersion);
        document = document with
        {
            Rates = document.Rates.Select(rate => rate with
            {
                Source = string.IsNullOrWhiteSpace(rate.Source) ? document.Source : rate.Source,
                CatalogVersion = string.IsNullOrWhiteSpace(rate.CatalogVersion)
                    ? document.CatalogVersion
                    : rate.CatalogVersion
            }).ToArray()
        };
        ValidateDocument(document);

        var json = JsonSerializer.Serialize(document, JsonOptions);
        await WriteAtomicAsync(exportPath, json, cancellationToken);
        return new LocalOperationResult(
            true,
            $"已导出费率目录 {document.CatalogVersion}（{document.Rates.Count} 条）。",
            exportPath);
    }

    public async Task<ImportedRateCatalog> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("费率目录文件不存在。", fullPath);
        }

        if (file.Length <= 0 || file.Length > MaximumCatalogBytes)
        {
            throw new InvalidDataException("费率目录必须是 1 MB 以内的非空 JSON 文件。");
        }

        RateCatalogDocument document;
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            document = await JsonSerializer.DeserializeAsync<RateCatalogDocument>(
                stream,
                JsonOptions,
                cancellationToken)
                ?? throw new InvalidDataException("费率目录内容为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("费率目录不是有效的 JSON。", exception);
        }

        ValidateDocument(document);
        var normalized = new AppSettings(
                CustomModelRates: document.Rates,
                IsRateCatalogPinned: true,
                PinnedRateCatalogVersion: document.CatalogVersion,
                PinnedRateCatalogSource: document.Source,
                PinnedRateCatalogBaseVersion: document.BaseCatalogVersion)
            .Validate()
            .Normalize()
            .CustomModelRates!
            .Select(rate => RestoreProvenance(rate, document))
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new InvalidDataException("费率目录没有可用的模型费率。");
        }

        return new ImportedRateCatalog(
            document.CatalogVersion.Trim(),
            document.Source.Trim(),
            normalized,
            string.IsNullOrWhiteSpace(document.BaseCatalogVersion)
                ? null
                : document.BaseCatalogVersion.Trim());
    }

    /// <summary>
    /// Fills in a rate's missing Source and CatalogVersion. A row priced exactly like
    /// a built-in gets that built-in's own provenance back, so exports written before
    /// the app recorded provenance still re-import as built-ins. Falling back to the
    /// document's provenance would misattribute every built-in whose catalog version
    /// differs from the one the export happened to be stamped with.
    /// </summary>
    private static ModelCreditRate RestoreProvenance(ModelCreditRate rate, RateCatalogDocument document)
    {
        var hasSource = !string.IsNullOrWhiteSpace(rate.Source);
        var hasVersion = !string.IsNullOrWhiteSpace(rate.CatalogVersion);
        if (hasSource && hasVersion)
        {
            return rate;
        }

        var builtIn = UsageCredits.FindBuiltInByPricing(rate);
        return rate with
        {
            Source = hasSource ? rate.Source : builtIn?.Source ?? document.Source.Trim(),
            CatalogVersion = hasVersion
                ? rate.CatalogVersion
                : builtIn?.CatalogVersion ?? document.CatalogVersion.Trim()
        };
    }

    private static void ValidateDocument(RateCatalogDocument document)
    {
        if (document.SchemaVersion != UsageCredits.RateCatalogSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持费率目录 schema {document.SchemaVersion}；当前仅支持 {UsageCredits.RateCatalogSchemaVersion}。");
        }

        if (string.IsNullOrWhiteSpace(document.CatalogVersion) || document.CatalogVersion.Trim().Length > 40)
        {
            throw new InvalidDataException("费率目录版本不能为空且不能超过 40 个字符。");
        }

        if (string.IsNullOrWhiteSpace(document.Source) || document.Source.Trim().Length > 200)
        {
            throw new InvalidDataException("费率目录来源不能为空且不能超过 200 个字符。");
        }

        if (document.BaseCatalogVersion?.Length > 40)
        {
            throw new InvalidDataException("费率目录基线版本不能超过 40 个字符。");
        }

        if (document.Rates is not { Count: > 0 }
            || document.Rates.Count > UsageCredits.MaximumCatalogRateCount)
        {
            throw new InvalidDataException(
                $"费率目录必须包含 1 到 {UsageCredits.MaximumCatalogRateCount} 条费率。");
        }

        var keys = new HashSet<(string Model, DateOnly? EffectiveFrom)>(RateKeyComparer.Instance);
        foreach (var rate in document.Rates)
        {
            if (rate is null)
            {
                throw new InvalidDataException("费率目录不能包含 null 项。");
            }

            if (string.IsNullOrWhiteSpace(rate.Model) || rate.Model.Trim().Length > 100)
            {
                throw new InvalidDataException("模型名称不能为空且不能超过 100 个字符。");
            }

            if (!keys.Add((UsageCredits.NormalizeModel(rate.Model), rate.EffectiveFrom)))
            {
                throw new InvalidDataException($"费率目录包含重复的模型与生效日期：{rate.Model} / {rate.EffectiveFrom?.ToString("yyyy-MM-dd") ?? "全部历史"}。");
            }

            ValidateRate(rate.InputCreditsPerMillion, "普通输入");
            ValidateRate(rate.CachedInputCreditsPerMillion, "缓存输入");
            ValidateRate(rate.OutputCreditsPerMillion, "输出");
            if (rate.Source?.Length > 200 || rate.CatalogVersion?.Length > 40)
            {
                throw new InvalidDataException("单条费率的来源或版本字段过长。");
            }

            if (!string.Equals(rate.MatchMode, "exact", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rate.MatchMode, "prefix", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("单条费率的 matchMode 只能是 exact 或 prefix。");
            }

        }
    }

    private static void ValidateRate(double value, string field)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1_000_000d)
        {
            throw new InvalidDataException($"{field}费率超出允许范围。");
        }
    }

    private void EnsureExportTargetIsAllowed(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.Equals(
                directory is null ? null : Path.TrimEndingDirectorySeparator(directory),
                _applicationDataDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = Path.GetFileName(fullPath);
        if (ReservedApplicationDataFileNames.Contains(fileName)
            || (fileName.StartsWith("session-index", StringComparison.OrdinalIgnoreCase)
                && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"不能将费率目录导出到应用活动数据文件“{fileName}”。");
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("费率目录目标路径无效。");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private sealed class RateKeyComparer : IEqualityComparer<(string Model, DateOnly? EffectiveFrom)>
    {
        public static RateKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Model, DateOnly? EffectiveFrom) x,
            (string Model, DateOnly? EffectiveFrom) y) =>
            string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase)
            && x.EffectiveFrom == y.EffectiveFrom;

        public int GetHashCode((string Model, DateOnly? EffectiveFrom) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Model), value.EffectiveFrom);
    }
}
