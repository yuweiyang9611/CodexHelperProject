using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class RateCatalogFileServiceTests
{
    [Fact]
    public async Task ExportAndImportAsync_RoundTripsVersionedRatesAndCatalogMetadata()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "rates.json");
            var service = new RateCatalogFileService();
            var rates = new[]
            {
                new ModelCreditRate(
                    "History_Model",
                    12.5,
                    1.25,
                    75,
                    new DateOnly(2026, 1, 1),
                    "vendor pricing",
                    "2026.1"),
                new ModelCreditRate(
                    "History_Model",
                    20,
                    2,
                    120,
                    new DateOnly(2026, 7, 1),
                    "vendor pricing",
                    "2026.2")
            };

            var export = await service.ExportAsync(rates, path);
            var imported = await service.ImportAsync(path);
            var exportedDocument = JsonSerializer.Deserialize<RateCatalogDocument>(
                await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.True(export.Success);
            Assert.Equal(Path.GetFullPath(path), export.Path);
            Assert.Equal("custom", imported.CatalogVersion);
            Assert.Equal("codexU 用户自定义", imported.Source);
            Assert.NotNull(exportedDocument);
            Assert.Equal(UsageCredits.CurrentCatalogVersion, exportedDocument.BaseCatalogVersion);
            Assert.Equal(UsageCredits.CurrentCatalogVersion, imported.BaseCatalogVersion);
            Assert.Contains(imported.Rates, rate =>
                rate.Model == "gpt-5.6-sol"
                && rate.CatalogVersion == UsageCredits.CurrentCatalogVersion
                && rate.MatchMode == "exact");
            Assert.Null(UsageCredits.FindRate(
                "gpt-5.2-premium",
                new DateOnly(2026, 7, 16),
                imported.Rates));
            var importedHistoryRates = imported.Rates
                .Where(rate => rate.Model == "history-model")
                .OrderBy(rate => rate.EffectiveFrom)
                .ToArray();
            Assert.Collection(
                importedHistoryRates,
                first =>
                {
                    Assert.Equal("history-model", first.Model);
                    Assert.Equal(new DateOnly(2026, 1, 1), first.EffectiveFrom);
                    Assert.Equal("2026.1", first.CatalogVersion);
                    Assert.Equal("vendor pricing", first.Source);
                    Assert.Equal(12.5, first.InputCreditsPerMillion);
                },
                second =>
                {
                    Assert.Equal("history-model", second.Model);
                    Assert.Equal(new DateOnly(2026, 7, 1), second.EffectiveFrom);
                    Assert.Equal("2026.2", second.CatalogVersion);
                    Assert.Equal("vendor pricing", second.Source);
                    Assert.Equal(20, second.InputCreditsPerMillion);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImportAsync_PinnedSnapshotDoesNotInjectCurrentRowsAndPreservesMetadata()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "pinned-rates.json");
            var service = new RateCatalogFileService();
            var rates = new[]
            {
                new ModelCreditRate("archive-model", 10, 1, 100)
            };

            await service.ExportAsync(
                rates,
                path,
                completeSnapshot: true,
                catalogVersion: "archive-v1",
                source: "archived vendor table",
                baseCatalogVersion: "vendor-base-v1");
            var imported = await service.ImportAsync(path);
            var document = JsonSerializer.Deserialize<RateCatalogDocument>(
                await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(document);
            var documentRate = Assert.Single(document.Rates);
            Assert.Equal("archive-model", documentRate.Model);
            Assert.DoesNotContain(document.Rates, UsageCredits.IsBuiltInRate);
            Assert.Equal("archive-v1", imported.CatalogVersion);
            Assert.Equal("archived vendor table", imported.Source);
            Assert.Equal("vendor-base-v1", imported.BaseCatalogVersion);
            Assert.Single(imported.Rates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAndReexportAsync_PreservesPinnedSnapshotWithMoreThanCustomRateLimit()
    {
        var root = CreateRoot();
        try
        {
            var importPath = Path.Combine(root, "large-archive.json");
            var exportPath = Path.Combine(root, "large-archive-reexport.json");
            var rates = Enumerable.Range(0, UsageCredits.MaximumCustomRateCount + 1)
                .Select(index => new ModelCreditRate(
                    $"archive-model-{index:D3}",
                    index + 1,
                    index + 0.1,
                    index + 2,
                    Source: "archived vendor table",
                    CatalogVersion: "archive-v1",
                    MatchMode: "exact"))
                .ToArray();
            var document = new RateCatalogDocument(
                UsageCredits.RateCatalogSchemaVersion,
                "archive-v1",
                "archived vendor table",
                DateTimeOffset.UtcNow,
                rates,
                "vendor-base-v1");
            await File.WriteAllTextAsync(
                importPath,
                JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var service = new RateCatalogFileService();
            var imported = await service.ImportAsync(importPath);
            await service.ExportAsync(
                imported.Rates,
                exportPath,
                completeSnapshot: true,
                catalogVersion: imported.CatalogVersion,
                source: imported.Source,
                baseCatalogVersion: imported.BaseCatalogVersion);
            var reimported = await service.ImportAsync(exportPath);

            Assert.Equal(UsageCredits.MaximumCustomRateCount + 1, imported.Rates.Count);
            Assert.Equal(imported.Rates, reimported.Rates);
            Assert.Equal("archive-v1", reimported.CatalogVersion);
            Assert.Equal("vendor-base-v1", reimported.BaseCatalogVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"model\":\"broken\",\"inputCreditsPerMillion\":1,\"cachedInputCreditsPerMillion\":1}")]
    [InlineData("{\"model\":\"broken\",\"inputCreditsPerMillion\":1,\"cachedInputCreditsPerMillion\":1,\"outputCreditPerMillion\":1}")]
    public async Task ImportAsync_RejectsMissingOrMisspelledRequiredRateFields(string rateJson)
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "invalid-fields.json");
            await File.WriteAllTextAsync(
                path,
                $$"""
                {"schemaVersion":1,"catalogVersion":"bad","source":"test","exportedAt":"2026-07-16T00:00:00Z","rates":[{{rateJson}}]}
                """);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => new RateCatalogFileService().ImportAsync(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_AllowsMaximumCustomRatesPlusInheritedBuiltInBaseline()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "inherited-baseline.json");
            var inheritedBaseline = UsageCredits.BuiltInRates
                .Select(rate => rate with { Source = null, CatalogVersion = null })
                .ToArray();
            var customRates = Enumerable.Range(0, UsageCredits.MaximumCustomRateCount)
                .Select(index => new ModelCreditRate(
                    $"custom-{index:D3}",
                    index + 1,
                    index + 0.5,
                    index + 2,
                    Source: "custom source",
                    CatalogVersion: "custom-v1"))
                .ToArray();
            var document = new RateCatalogDocument(
                UsageCredits.RateCatalogSchemaVersion,
                UsageCredits.CurrentCatalogVersion,
                UsageCredits.CurrentCatalogSource,
                DateTimeOffset.UtcNow,
                inheritedBaseline.Concat(customRates).ToArray(),
                UsageCredits.CurrentCatalogVersion);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var imported = await new RateCatalogFileService().ImportAsync(path);

            Assert.Equal(
                UsageCredits.MaximumCustomRateCount + UsageCredits.BuiltInRates.Count,
                imported.Rates.Count);
            Assert.Equal(UsageCredits.BuiltInRates.Count, imported.Rates.Count(UsageCredits.IsBuiltInRate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsNullAndDuplicateRateRows()
    {
        var root = CreateRoot();
        try
        {
            var nullPath = Path.Combine(root, "null-rate.json");
            await File.WriteAllTextAsync(
                nullPath,
                """
                {"schemaVersion":1,"catalogVersion":"bad","source":"test","exportedAt":"2026-07-16T00:00:00Z","rates":[null]}
                """);

            var service = new RateCatalogFileService();
            var nullException = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(nullPath));
            Assert.Contains("null", nullException.Message, StringComparison.OrdinalIgnoreCase);

            var duplicatePath = Path.Combine(root, "duplicate-rate.json");
            await File.WriteAllTextAsync(
                duplicatePath,
                """
                {"schemaVersion":1,"catalogVersion":"bad","source":"test","exportedAt":"2026-07-16T00:00:00Z","rates":[
                  {"model":"History_Model","inputCreditsPerMillion":1,"cachedInputCreditsPerMillion":1,"outputCreditsPerMillion":1,"effectiveFrom":"2026-01-01"},
                  {"model":"history-model","inputCreditsPerMillion":2,"cachedInputCreditsPerMillion":2,"outputCreditsPerMillion":2,"effectiveFrom":"2026-01-01"}
                ]}
                """);

            var duplicateException = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.ImportAsync(duplicatePath));
            Assert.Contains("重复", duplicateException.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImportAsync_PreservesMaximumOverridesAndBuiltInBaseline()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "maximum-rates.json");
            var overrides = Enumerable.Range(0, UsageCredits.MaximumCustomRateCount)
                .Select(index => new ModelCreditRate(
                    $"custom-model-{index:D3}",
                    index + 1,
                    index + 0.5,
                    index + 2,
                    new DateOnly(2026, 1, 1),
                    "test",
                    "custom-v1",
                    "exact"))
                .ToArray();

            var service = new RateCatalogFileService();
            await service.ExportAsync(overrides, path);
            var imported = await service.ImportAsync(path);

            Assert.Equal(
                UsageCredits.MaximumCustomRateCount + UsageCredits.BuiltInRates.Count,
                imported.Rates.Count);
            Assert.Equal(
                UsageCredits.MaximumCustomRateCount,
                imported.Rates.Count(rate => !UsageCredits.IsBuiltInRate(rate)));
            Assert.Equal(
                UsageCredits.BuiltInRates.Count,
                imported.Rates.Count(UsageCredits.IsBuiltInRate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImportAsync_StampsLegacyCustomMetadataWithoutClaimingBuiltInProvenance()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "legacy-custom-rate.json");
            var service = new RateCatalogFileService();
            var legacyRate = new ModelCreditRate(
                "legacy-custom-model",
                10,
                1,
                100,
                new DateOnly(2025, 1, 1),
                MatchMode: "exact");

            await service.ExportAsync([legacyRate], path);
            var imported = await service.ImportAsync(path);
            var exportedDocument = JsonSerializer.Deserialize<RateCatalogDocument>(
                await File.ReadAllTextAsync(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(exportedDocument);
            Assert.Equal(UsageCredits.CustomCatalogVersion, exportedDocument.CatalogVersion);
            Assert.Equal(UsageCredits.CustomCatalogSource, exportedDocument.Source);
            var exportedLegacyRate = Assert.Single(
                exportedDocument.Rates,
                rate => rate.Model == "legacy-custom-model");
            Assert.Equal(UsageCredits.CustomCatalogVersion, exportedLegacyRate.CatalogVersion);
            Assert.Equal(UsageCredits.CustomCatalogSource, exportedLegacyRate.Source);
            Assert.NotEqual(UsageCredits.CurrentCatalogVersion, exportedLegacyRate.CatalogVersion);
            Assert.NotEqual(UsageCredits.CurrentCatalogSource, exportedLegacyRate.Source);

            var importedLegacyRate = Assert.Single(
                imported.Rates,
                rate => rate.Model == "legacy-custom-model");
            Assert.Equal(UsageCredits.CustomCatalogVersion, importedLegacyRate.CatalogVersion);
            Assert.Equal(UsageCredits.CustomCatalogSource, importedLegacyRate.Source);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("settings.json")]
    [InlineData("settings.json.bak")]
    [InlineData("todos.json")]
    [InlineData("todos.json.bak")]
    [InlineData("update-check.json")]
    [InlineData("startup.log")]
    [InlineData("session-index.json")]
    [InlineData("session-index-v3.json")]
    public async Task ExportAsync_RejectsReservedApplicationDataTargetsWithoutChangingThem(
        string fileName)
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, fileName);
            const string originalContent = "original application data";
            await File.WriteAllTextAsync(path, originalContent);
            var service = new RateCatalogFileService(root);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ExportAsync(
                    [new ModelCreditRate("test-model", 1, 0.1, 2)],
                    path));

            Assert.Equal(originalContent, await File.ReadAllTextAsync(path));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_NormalDirectoryForcesJsonExtensionAndExportsSuccessfully()
    {
        var applicationDataRoot = CreateRoot();
        var exportRoot = CreateRoot();
        try
        {
            var requestedPath = Path.Combine(exportRoot, "portable-rate-catalog.backup");
            var expectedPath = Path.Combine(exportRoot, "portable-rate-catalog.json");
            var service = new RateCatalogFileService(applicationDataRoot);

            var result = await service.ExportAsync(
                [new ModelCreditRate("test-model", 1, 0.1, 2)],
                requestedPath);

            Assert.True(result.Success);
            Assert.Equal(expectedPath, result.Path);
            Assert.True(File.Exists(expectedPath));
            Assert.False(File.Exists(requestedPath));
            var imported = await service.ImportAsync(expectedPath);
            Assert.Contains(imported.Rates, rate => rate.Model == "test-model");
        }
        finally
        {
            Directory.Delete(applicationDataRoot, recursive: true);
            Directory.Delete(exportRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RejectsUnsupportedSchemaVersion()
    {
        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "unsupported-schema.json");
            var document = new RateCatalogDocument(
                UsageCredits.RateCatalogSchemaVersion + 1,
                "future-catalog",
                "vendor pricing",
                DateTimeOffset.UtcNow,
                [
                    new ModelCreditRate(
                        "history-model",
                        10,
                        1,
                        100,
                        new DateOnly(2026, 1, 1),
                        "vendor pricing",
                        "future-catalog")
                ]);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(
                    document,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new RateCatalogFileService().ImportAsync(path));

            Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_RestoresProvenanceOfBuiltInsFromEveryCatalogLineage()
    {
        // A catalog exported before the app recorded provenance carries no Source or
        // CatalogVersion. Backfilling those from the document only ever suits rows of
        // the document's own lineage; the built-in catalog spans several, so rows from
        // any other one would be misattributed and stop counting as built-in.
        var lineages = UsageCredits.BuiltInRates
            .Select(rate => rate.CatalogVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(lineages.Length > 1, "This guard is meaningless with a single built-in lineage.");

        var root = CreateRoot();
        try
        {
            var path = Path.Combine(root, "legacy-export.json");
            var stripped = UsageCredits.BuiltInRates
                .Select(rate => rate with { Source = null, CatalogVersion = null })
                .ToArray();
            var document = new RateCatalogDocument(
                UsageCredits.RateCatalogSchemaVersion,
                UsageCredits.CurrentCatalogVersion,
                UsageCredits.CurrentCatalogSource,
                DateTimeOffset.UtcNow,
                stripped,
                UsageCredits.CurrentCatalogVersion);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var imported = await new RateCatalogFileService().ImportAsync(path);

            Assert.All(imported.Rates, rate => Assert.True(
                UsageCredits.IsBuiltInRate(rate),
                $"{rate.Model} ({rate.CatalogVersion ?? "null"}) lost its built-in identity on import."));
            Assert.Equal(
                lineages.Order(StringComparer.Ordinal),
                imported.Rates.Select(rate => rate.CatalogVersion).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-rate-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
