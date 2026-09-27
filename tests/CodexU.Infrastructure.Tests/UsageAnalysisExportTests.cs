using System.Globalization;
using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class UsageAnalysisExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codexu-analysis-export-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "data");
    private LocalDataManagementService Service => new(new AppSettingsStore(Data), new TodoStore(Data), Data);

    [Fact]
    public async Task Json_UsesUnifiedAllHistoryTotals_SeparatesOldEstimates_AndExcludesIdentifiers()
    {
        var path = Path.Combine(_root, "usage.json");
        var result = await Service.ExportUsageAnalysisAsync(Analysis(), path, "json");
        Assert.True(result.Success);
        var text = await File.ReadAllTextAsync(path);
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("当前工具全部本机历史", root.GetProperty("scope").GetString());
        Assert.Equal("codex", root.GetProperty("runtime").GetString());
        Assert.Equal(180, root.GetProperty("totals").GetProperty("tokens").GetInt64());
        Assert.Equal(0.004, root.GetProperty("totals").GetProperty("creditsUsed").GetDouble(), 8);
        var days = root.GetProperty("dailyUsage").EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, days[0].GetProperty("totals").GetProperty("creditsUsed").ValueKind);
        Assert.Equal(2, root.GetProperty("legacyEstimates").GetArrayLength());
        Assert.Equal(99, root.GetProperty("legacyEstimates")[1].GetProperty("creditsUsed").GetDouble());
        Assert.Contains(root.GetProperty("projects").EnumerateArray(), p => p.GetProperty("name").GetString() == "safe-project");
        Assert.All(root.GetProperty("projects").EnumerateArray(), p => Assert.False(p.TryGetProperty("id", out _)));
        Assert.Contains(root.GetProperty("models").EnumerateArray(), m => m.GetProperty("model").GetString() == "model-a");
        Assert.False(root.TryGetProperty("sessions", out _));
        Assert.False(root.TryGetProperty("unattributed", out _));
        Assert.False(root.TryGetProperty("availableProjects", out _));
        Assert.False(root.TryGetProperty("diagnostics", out _));
        Assert.DoesNotContain("private-session", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private-parent-directory", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Csv_LeavesMissingAmountsBlank_AndMakesWholeDayEstimatesNonAdditive()
    {
        var path = Path.Combine(_root, "usage.csv");
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await Service.ExportUsageAnalysisAsync(Analysis(), path, "csv");
        }
        finally { CultureInfo.CurrentCulture = previous; }
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal("date,tokens,credits_used,unrated_tokens,unattributed_tokens,legacy_whole_day_estimate_not_additive", lines[0]);
        Assert.Equal("2025-01-09,80,,80,30,12.5", lines[1]);
        Assert.Equal("2025-01-10,100,0.004,60,60,99", lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task CancelledExport_PreservesPreviousReport_AndCleansTemporaryFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "usage.json");
        await File.WriteAllTextAsync(path, "previous-report");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ExportUsageAnalysisAsync(Analysis(), path, "json", new CancellationToken(true)));
        Assert.Equal("previous-report", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("csv")]
    public async Task FilteredExportIncludesScopeAndOffPageRatesWithoutPrivatePaths(string format)
    {
        var from = new DateOnly(2025, 1, 10);
        var query = new UsageAnalysisRequest(From: from, Model: "model-a", Project: "D:/private-parent-directory/safe-project", PageSize: 1);
        var rate = new ModelCreditRate("model-a", 100, 10, 200, new(2020, 1, 1), "test-source", "v1");
        var data = UsageHistoryProjection.BuildAnalysis(Enumerable.Range(0, 30).Select(i => new AttributedUsage(
            $"private-session-{i}", query.Project, from.AddDays(i), "model-a", new(40, 0, 0, 0, 40), 1,
            AvailableFields: UsageBreakdownFields.Input | UsageBreakdownFields.Output | UsageBreakdownFields.CachedInput)).ToArray(),
            [], [rate, rate with { EffectiveFrom = from.AddDays(10), CatalogVersion = "v2" }], true);
        var result = UsageAnalysisQuery.Execute(data, query);
        Assert.Single(result.Sessions);
        var path = Path.Combine(_root, "filtered." + format);
        await Service.ExportUsageAnalysisAsync(result, path, format, selection: query);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("v1", text);
        Assert.Contains("v2", text);
        Assert.Contains("test-source", text);
        Assert.DoesNotContain("private-parent-directory", text);
        Assert.DoesNotContain("private-session", text);
        if (format == "json")
        {
            using var json = JsonDocument.Parse(text);
            Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1200, json.RootElement.GetProperty("totals").GetProperty("tokens").GetInt64());
            Assert.Equal(30, json.RootElement.GetProperty("dailyUsage").GetArrayLength());
            var filters = json.RootElement.GetProperty("filters");
            Assert.Equal("2025-01-10", filters.GetProperty("from").GetString());
            Assert.Equal("safe-project", filters.GetProperty("project").GetString());
            Assert.True(filters.GetProperty("projectPathOmitted").GetBoolean());
        }
        else
        {
            using var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(new StringReader(text));
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            Assert.Equal(9, parser.ReadFields()!.Length);
            var metadata = parser.ReadFields()!;
            Assert.Equal(9, metadata.Length);
            Assert.Equal("metadata", metadata[0]);
            using var metadataJson = JsonDocument.Parse(metadata[7]);
            Assert.Equal("2025-01-10", metadataJson.RootElement.GetProperty("filters").GetProperty("from").GetString());
            using var ratesJson = JsonDocument.Parse(metadata[8]);
            Assert.Equal(2, ratesJson.RootElement.GetArrayLength());
            var days = 0;
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields()!;
                Assert.Equal(9, fields.Length);
                Assert.Equal("day", fields[0]);
                days++;
            }
            Assert.Equal(30, days);
        }
    }

    [Fact]
    public async Task EmptyFilteredCsvStillRecordsSelection()
    {
        var query = new UsageAnalysisRequest(From: new(2099, 1, 1), Model: "=untrusted,model");
        var path = Path.Combine(_root, "empty.csv");
        await Service.ExportUsageAnalysisAsync(UsageAnalysisQuery.Execute(new([], []), query), path, "csv", selection: query);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("metadata,", text);
        Assert.Contains("2099-01-01", text);
        Assert.DoesNotContain("day,", text);
        Assert.Contains("\"\"model\"\"", text); // JSON is enclosed as one escaped CSV cell, never a formula cell.
    }

    [Fact]
    public async Task UnknownProjectExportAcceptsTheSameCaseInsensitiveSentinelAsQuery()
    {
        var query = new UsageAnalysisRequest(Project: "__UNKNOWN__");
        var path = Path.Combine(_root, "unknown.json");
        await Service.ExportUsageAnalysisAsync(UsageAnalysisQuery.Execute(new([], []), query), path, "json", selection: query);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var filters = json.RootElement.GetProperty("filters");
        Assert.Equal("未知项目", filters.GetProperty("project").GetString());
        Assert.False(filters.GetProperty("projectPathOmitted").GetBoolean());
    }

    [Fact]
    public async Task Export_RejectsManagedTargetsAndDatedQueries()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ExportUsageAnalysisAsync(Analysis(), Path.Combine(Data, "settings.json"), "json"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service.ExportUsageAnalysisAsync(Analysis() with { From = new(2025, 1, 9) }, Path.Combine(_root, "filtered.json"), "json"));
        Assert.False(File.Exists(Path.Combine(_root, "filtered.json")));
    }

    private static UsageAnalysisResult Analysis()
    {
        var old = new DateOnly(2025, 1, 9);
        var day = old.AddDays(1);
        const string project = "D:/private-parent-directory/safe-project";
        var data = UsageHistoryProjection.BuildAnalysis([
            new("private-session-a", project, old, "model-b", new(50, 0, 0, 0, 50), 1, Title: "private-title-a"),
            new("private-session-b", project, day, "model-a", new(40, 0, 0, 0, 40), 1, Title: "private-title-b",
                AvailableFields: UsageBreakdownFields.Input | UsageBreakdownFields.Output | UsageBreakdownFields.CachedInput)],
            [new(old, new(80, 0, 0, 0, 80), 12.5, 0, DataQuality.Approximate), new(day, new(100, 0, 0, 0, 100), 99, 0, DataQuality.Approximate)],
            [new("model-a", 100, 10, 200, new(2020, 1, 1))], true);
        return UsageAnalysisQuery.Execute(data, new(), ["private-parent-directory diagnostic"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
