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
