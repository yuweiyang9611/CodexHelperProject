using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class AntigravityQuotaReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesSeparateGroupsWithZeroAndUnknownValues()
    {
        var result = AntigravityQuotaReader.Parse(new(0, Envelope("""
            [
              {"name":"Gemini","description":"not included","buckets":[
                {"id":"gemini-weekly","name":"Gemini Weekly","window":"weekly","remaining_fraction":0,
                 "reset_time":"2026-11-28T08:10:41+09:00"},
                {"id":"gemini-5h","window":"5h","remaining_fraction":0.723,
                 "reset_time":"2026-11-27T03:00:00Z"}]},
              {"name":"Claude / GPT","buckets":[
                {"name":"Other","remaining_amount":2.5,"reset_time":"2026-11-28T13:00:00"},
                {"window":"5h","reset_time":"2026-11-27T10:00:00Z"},
                {"name":"Disabled window","disabled":true}]}
            ]
            """)), Now);

        Assert.Equal(AntigravityQuotaStatus.Ready, result.Status);
        Assert.Equal(Now, result.ObservedAt);
        Assert.Collection(result.Groups,
            first =>
            {
                Assert.Equal("Gemini", first.Name);
                Assert.Collection(first.Buckets,
                    weekly =>
                    {
                        Assert.Equal("每周", weekly.Label);
                        Assert.Equal(0d, weekly.RemainingPercent);
                        Assert.Equal(new DateTimeOffset(2026, 11, 28, 8, 10, 41, TimeSpan.FromHours(9)), weekly.ResetsAt);
                    },
                    rolling =>
                    {
                        Assert.Equal("5 小时", rolling.Label);
                        Assert.Equal(72.3d, rolling.RemainingPercent);
                    });
            },
            second =>
            {
                Assert.Equal("Claude / GPT", second.Name);
                Assert.Collection(second.Buckets,
                    amount =>
                    {
                        Assert.Equal(2.5d, amount.RemainingAmount);
                        Assert.Null(amount.RemainingPercent);
                        Assert.Null(amount.ResetsAt);
                    },
                    reset =>
                    {
                        Assert.NotNull(reset.ResetsAt);
                        Assert.Null(reset.RemainingPercent);
                    },
                    disabled => Assert.True(disabled.Disabled));
            });
        var wire = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("not included", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("gemini-weekly", wire, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "SUCCESS", "usage", 0)]
    [InlineData(0, "FAILURE", "usage", 0)]
    [InlineData(0, "SUCCESS", "chat", 0)]
    public void RejectsUnverifiedCommandResults(int exitCode, string status, string command, int turns)
    {
        var output = Envelope("[]", status, command, turns);
        Assert.Equal(AntigravityQuotaStatus.Unavailable, AntigravityQuotaReader.Parse(new(exitCode, output), Now).Status);
    }

    [Theory]
    // Observed: a /usage that fell through to a prompt exits 3 with status ERROR and one turn.
    [InlineData(3, """{"status":"ERROR","num_turns":1}""")]
    [InlineData(0, """{"status":"SUCCESS","num_turns":1,"command":{"name":"usage","data":{"groups":[]}}}""")]
    [InlineData(0, """{"status":"SUCCESS","num_turns":0,"usage":{"input_tokens":11000,"output_tokens":0},"command":{"name":"usage","data":{"groups":[]}}}""")]
    public void AModelTurnIsReportedEvenWhenTheRunFailed(int exitCode, string output)
    {
        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, AntigravityQuotaReader.Parse(new(exitCode, output), Now).Status);
    }

    [Fact]
    public void ZeroTokenUsageIsAnOrdinaryUsageAnswer()
    {
        var output = Envelope("""[{"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":0.5}]}]""")
            .Replace("\"num_turns\":0", "\"num_turns\":0,\"usage\":{\"input_tokens\":0,\"total_tokens\":0}", StringComparison.Ordinal);
        Assert.Equal(AntigravityQuotaStatus.Ready, AntigravityQuotaReader.Parse(new(0, output), Now).Status);
    }

    [Fact]
    public void AGroupWithoutBucketsDoesNotHideTheOthers()
    {
        var result = AntigravityQuotaReader.Parse(new(0, Envelope("""
            [{"name":"Credits","balance":3},
             {"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":0.4}]}]
            """)), Now);

        Assert.Equal(AntigravityQuotaStatus.Ready, result.Status);
        Assert.Equal("Gemini", Assert.Single(result.Groups).Name);
    }

    [Fact]
    public async Task AVersionThatRanAModelTurnIsNotQueriedAgainUntilTheVersionChanges()
    {
        var clock = new ManualClock();
        var version = "1.5.18";
        var usageCalls = 0;
        var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, version + "\n"));
            usageCalls++;
            return Task.FromResult(usageCalls == 1
                ? new ClaudeCliAuthCommandResult(3, """{"status":"ERROR","num_turns":1}""")
                : new ClaudeCliAuthCommandResult(0, Envelope("""[{"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":1}]}]""")));
        }, clock);

        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await reader.ReadAsync()).Status);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await reader.ReadAsync()).Status);
        Assert.Equal(1, usageCalls);

        version = "1.5.19";
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AntigravityQuotaStatus.Ready, (await reader.ReadAsync()).Status);
        Assert.Equal(2, usageCalls);
    }

    [Fact]
    public void ValidResponseWithNoUsableBucketsIsDistinctFromAFailedQuery()
    {
        var response = AntigravityQuotaReader.Parse(new(0, Envelope("""
            [{"name":"Gemini","buckets":[
              {"window":"weekly","remaining_fraction":-0.01},
              {"window":"5h","remaining_fraction":1.1},
              {"window":"5h","remaining_fraction":"50"}
            ]}]
            """)), Now);
        Assert.Equal(AntigravityQuotaStatus.NoQuota, response.Status);
        Assert.Equal(Now, response.ObservedAt);
        Assert.Empty(response.Groups);

        var invalid = AntigravityQuotaReader.Parse(new(0, Envelope("[]").Replace(
            "\"num_turns\":0", "\"num_turns\":1,\"num_turns\":0", StringComparison.Ordinal)), Now);
        Assert.Equal(AntigravityQuotaStatus.Unavailable, invalid.Status);
    }

    [Theory]
    [InlineData("1.1.10")]
    [InlineData("unknown")]
    public async Task VersionGatePreventsQueriesWithPotentialModelFallback(string version)
    {
        var arguments = new List<string>();
        var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
        {
            arguments.AddRange(info.ArgumentList);
            return Task.FromResult(new ClaudeCliAuthCommandResult(0, version));
        }, new ManualClock());

        Assert.Equal(AntigravityQuotaStatus.UnsupportedVersion, (await reader.ReadAsync()).Status);
        Assert.Equal(["--version"], arguments);
    }

    [Fact]
    public async Task CoalescesConcurrentReadsAndDoesNotReuseStaleQuotaAfterFailure()
    {
        var clock = new ManualClock();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ClaudeCliAuthCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var quotaCalls = 0;
        var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
        {
            Assert.False(info.UseShellExecute);
            Assert.True(info.CreateNoWindow && info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
            Assert.True(Path.IsPathFullyQualified(info.WorkingDirectory));
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.17\r\n"));

            Assert.Equal(["-p", "/usage", "--output-format", "json", "--print-timeout", "12s"], info.ArgumentList);
            if (++quotaCalls == 2)
                return Task.FromResult(new ClaudeCliAuthCommandResult(1, "account data not shown"));
            started.SetResult();
            return finish.Task;
        }, clock);

        var first = reader.ReadAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = reader.ReadAsync();
        finish.SetResult(new(0, Envelope("""[{"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":1}]}]""")));
        var results = await Task.WhenAll(first, second);
        Assert.Same(results[0], results[1]);
        Assert.Equal(1, quotaCalls);
        Assert.Equal(AntigravityQuotaStatus.Ready, results[0].Status);

        clock.Advance(TimeSpan.FromSeconds(16));
        var failed = await reader.ReadAsync();
        Assert.Equal(AntigravityQuotaStatus.Unavailable, failed.Status);
        Assert.Empty(failed.Groups);
        Assert.Equal(2, quotaCalls);
    }

    private static string Envelope(string groups, string status = "SUCCESS", string command = "usage", int turns = 0) =>
        $$"""{"status":"{{status}}","num_turns":{{turns}},"command":{"name":"{{command}}","data":{"groups":{{groups}} } } }""";

    private sealed class ManualClock : TimeProvider
    {
        private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => Now + _elapsed;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan elapsed) => _elapsed += elapsed;
    }
}
