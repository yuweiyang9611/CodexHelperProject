using System.Text.Json;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeDesktopQuotaReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_UsesDesktopHistoryWithoutInventingAccountOrResets()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-2).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 23 } }]);

        var snapshot = await fixture.Reader().ReadAsync();

        Assert.NotNull(snapshot.PrimaryQuota);
        Assert.Equal(29, snapshot.PrimaryQuota.UsedPercent);
        Assert.Equal(23, snapshot.SecondaryQuota?.UsedPercent);
        Assert.Equal(Now.AddMinutes(-2), snapshot.PrimaryQuota.MeasuredAt);
        Assert.Equal("claude-desktop-history", snapshot.PrimaryQuota.Source);
        Assert.Equal("claude-desktop-history", snapshot.SecondaryQuota!.Source);
        Assert.Null(snapshot.PrimaryQuota.ResetsAt);
        Assert.Null(snapshot.SecondaryQuota!.ResetsAt);
        Assert.Null(snapshot.Account);
    }

    [Fact]
    public async Task ReadAsync_UsesNewestWholeSampleAndKeepsMissingFiveHourUnknown()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync(
        [
            new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 23 } },
            new { t = Now.AddMinutes(-5).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 21 } }
        ]);

        var result = await fixture.Reader().ReadAsync();

        Assert.Null(result.PrimaryQuota);
        Assert.Equal(23, result.SecondaryQuota?.UsedPercent);
        Assert.Null(result.Account);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(5)]
    public async Task ReadAsync_AcceptsBoundaryAgeAndExplicitZero(int offsetMinutes)
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(offsetMinutes).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 0, sd = 100 } }]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(0, result.PrimaryQuota?.UsedPercent);
        Assert.Equal(100, result.SecondaryQuota?.UsedPercent);
    }

    [Theory]
    [InlineData(-31, "过期")]
    [InlineData(6, "未来")]
    public async Task ReadAsync_RejectsStaleOrFutureSamplesEvenWhenFileWasJustWritten(int offsetMinutes, string diagnostic)
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(offsetMinutes).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 23 } }]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("old-version")]
    [InlineData("invalid-json")]
    [InlineData("root-array")]
    [InlineData("version-string")]
    [InlineData("samples-object")]
    [InlineData("sample-scalar")]
    [InlineData("usage-array")]
    [InlineData("seconds-timestamp")]
    [InlineData("string-timestamp")]
    [InlineData("unknown-org")]
    [InlineData("too-many-samples")]
    [InlineData("too-large")]
    [InlineData("negative-percent")]
    [InlineData("over-percent")]
    [InlineData("string-percent")]
    [InlineData("null-percent")]
    [InlineData("nonfinite-percent")]
    public async Task ReadAsync_RejectsUnsupportedOrUnboundedHistory(string scenario)
    {
        using var fixture = new Fixture();
        var timestamp = Now.ToUnixTimeMilliseconds();
        var sample = JsonSerializer.Serialize(new { t = timestamp, org = "fixture-org", u = new { fh = 29, sd = 23 } });
        var json = "{\"version\":2,\"samples\":[" + sample + "]}";
        json = scenario switch
        {
            "old-version" => json.Replace("\"version\":2", "\"version\":1"),
            "invalid-json" => "{",
            "root-array" => "[]",
            "version-string" => json.Replace("\"version\":2", "\"version\":\"2\""),
            "samples-object" => "{\"version\":2,\"samples\":{}}",
            "sample-scalar" => "{\"version\":2,\"samples\":[42]}",
            "usage-array" => json.Replace("{\"fh\":29,\"sd\":23}", "[]"),
            "seconds-timestamp" => json.Replace(timestamp.ToString(), Now.ToUnixTimeSeconds().ToString()),
            "string-timestamp" => json.Replace(timestamp.ToString(), $"\"{timestamp}\""),
            "unknown-org" => json.Replace("\"fixture-org\"", "null"),
            "too-many-samples" => "{\"version\":2,\"samples\":[" + string.Join(',', Enumerable.Repeat(sample, 10_001)) + "]}",
            "too-large" => json + new string(' ', 2 * 1024 * 1024),
            "negative-percent" => json.Replace("\"fh\":29", "\"fh\":-1"),
            "over-percent" => json.Replace("\"fh\":29", "\"fh\":101"),
            "string-percent" => json.Replace("\"fh\":29", "\"fh\":\"29\""),
            "null-percent" => json.Replace("\"fh\":29", "\"fh\":null"),
            "nonfinite-percent" => json.Replace("\"fh\":29", "\"fh\":1e400"),
            _ => throw new ArgumentException(scenario)
        };
        await fixture.WriteRawHistoryAsync(json);

        var result = await fixture.Reader().ReadAsync();

        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
        Assert.Null(result.Account);
        Assert.Contains(result.Diagnostics, item => item.Contains("Claude Desktop", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAsync_RejectsMultipleOrganizationsWithinOrAcrossFiles(bool separateFiles)
    {
        using var fixture = new Fixture();
        var first = new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "fixture-org-one", u = new { fh = 29, sd = 23 } };
        var second = new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org-two", u = new { fh = 79, sd = 73 } };
        await fixture.WriteHistoryAsync(separateFiles ? [first] : [first, second]);
        if (separateFiles) await fixture.WriteHistoryAsync([second], "claude_other");

        var result = await fixture.Reader().ReadAsync();

        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("多个组织", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, item => item.Contains("fixture-org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_IgnoresModelAndExtraUsageWindowsInsteadOfTreatingThemAsGeneralQuota()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { so = 29, sn = 23, xu = 40 } }]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
    }

    [Fact]
    public async Task ReadAsync_NewestSampleWithoutGeneralQuotaDoesNotReviveOlderWindows()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync(
        [
            new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 23 } },
            new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { so = 40 } }
        ]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Null(result.SecondaryQuota);
    }

    [Fact]
    public async Task ReadAsync_SelectsNewestWholeFileWithinOneOrganization()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-2).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 23 } }]);
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 24 } }], "claude_other");
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Equal(24, result.SecondaryQuota?.UsedPercent);
    }

    [Fact]
    public async Task ReadAsync_RejectsConflictingSameTimeSamples()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29 } }]);
        await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 79 } }], "claude_other");
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("冲突", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_ExplicitNullDesktopRootDisablesDiscovery()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29 } }]);
        var reader = new ClaudeCodeUsageReader(fixture.Paths with { ClaudeDesktopDataDirectory = null },
            applicationDataDirectory: Path.Combine(fixture.Root, "data"))
        { Clock = new FixedClock() };
        Assert.Null((await reader.ReadAsync()).PrimaryQuota);
    }

    [Fact]
    public async Task ReadAsync_PrefersNewerDesktopSampleOverCopiedOlderStatuslineWithoutMixingPlanOrWindows()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 23 } }]);
        await fixture.WriteStatuslineAsync(Now.AddMinutes(-10));
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Equal(23, result.SecondaryQuota?.UsedPercent);
        Assert.Null(result.SecondaryQuota!.ResetsAt);
        Assert.Null(result.Account);
    }

    [Fact]
    public async Task ReadAsync_PreservesNewerStatuslineAsAWhole()
    {
        using var fixture = new Fixture();
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-10).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { fh = 29, sd = 23 } }]);
        await fixture.WriteStatuslineAsync(Now.AddMinutes(-1));
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(45, result.PrimaryQuota?.UsedPercent);
        Assert.Null(result.SecondaryQuota);
        Assert.Equal("pro", result.Account?.PlanType);
        Assert.Equal(Now.AddMinutes(-1), result.PrimaryQuota!.MeasuredAt);
        Assert.Equal(Now.AddHours(2), result.PrimaryQuota.ResetsAt);
    }

    [Fact]
    public async Task ReadAsync_InvalidFirstStatuslineDoesNotHideValidFallback()
    {
        using var fixture = new Fixture();
        await fixture.WriteStatuslineAsync(Now.AddMinutes(-1));
        var invalid = Path.Combine(fixture.Root, "data", "claude-code", "statusline-snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
        await File.WriteAllTextAsync(invalid, "{");
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(45, result.PrimaryQuota?.UsedPercent);
        Assert.Null(result.SecondaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("快照无法读取", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_FutureStatuslineDoesNotReplaceValidDesktopHistory()
    {
        using var fixture = new Fixture();
        await fixture.WriteStatuslineAsync(Now.AddMinutes(6));
        await fixture.WriteHistoryAsync([new { t = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 23 } }]);
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.PrimaryQuota);
        Assert.Equal(23, result.SecondaryQuota?.UsedPercent);
        Assert.Equal("claude-desktop-history", result.SecondaryQuota!.Source);
        Assert.Null(result.Account);
    }

    [Fact]
    public async Task ReadAsync_CorruptDesktopCandidateDoesNotHideStatuslineOrGuessOtherDesktopAccount()
    {
        using var fixture = new Fixture();
        await fixture.WriteStatuslineAsync(Now.AddMinutes(-1));
        await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 23 } }]);
        await fixture.WriteRawHistoryAsync("{", "claude_other");
        var result = await fixture.Reader().ReadAsync();
        Assert.Equal(45, result.PrimaryQuota?.UsedPercent);
        Assert.Null(result.SecondaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("Claude Desktop 额度：本地历史无法安全读取", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_RejectsExcessiveCandidateDirectories()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 17; index++)
            await fixture.WriteHistoryAsync([new { t = Now.ToUnixTimeMilliseconds(), org = "fixture-org", u = new { sd = 23 } }], "claude_fixture_" + index);
        var result = await fixture.Reader().ReadAsync();
        Assert.Null(result.SecondaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("目录数量超过安全上限", StringComparison.Ordinal));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-desktop-quota-" + Guid.NewGuid().ToString("N"));
        internal string DesktopRoot => Path.Combine(Root, "Claude-Data");
        internal CodexPaths Paths => new(Root, Root, Path.Combine(Root, "missing.sqlite"),
            Path.Combine(Root, "sessions"), Path.Combine(Root, "archive"), Path.Combine(Root, "automations"),
            Path.Combine(Root, "goals.sqlite"), Path.Combine(Root, ".claude"), DesktopRoot);
        internal ClaudeCodeUsageReader Reader() => new(Paths, applicationDataDirectory: Path.Combine(Root, "data")) { Clock = new FixedClock() };
        internal async Task WriteHistoryAsync(object[] samples, string package = "claude_fixture", int version = 2)
            => await WriteRawHistoryAsync(JsonSerializer.Serialize(new { version, samples }), package);
        internal async Task WriteRawHistoryAsync(string json, string package = "claude_fixture")
        {
            var file = Path.Combine(DesktopRoot, package, "plan-usage-history.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, json);
        }
        internal async Task WriteStatuslineAsync(DateTimeOffset capturedAt)
        {
            var file = Path.Combine(Paths.ClaudeDirectory, "statusline-snapshot.json");
            Directory.CreateDirectory(Paths.ClaudeDirectory);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                capturedAt,
                planType = "pro",
                primary = new { usedPercent = 45, windowDurationMinutes = 300, resetsAt = Now.AddHours(2) }
            }));
            File.SetLastWriteTimeUtc(file, Now.UtcDateTime);
        }
        public void Dispose()
        {
            UsageReadContext.Invalidate(Path.Combine(Root, "data"));
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
