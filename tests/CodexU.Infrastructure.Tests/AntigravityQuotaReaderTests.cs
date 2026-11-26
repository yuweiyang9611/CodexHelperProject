using System.Diagnostics;
using System.Text;
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

    [Theory]
    [InlineData(AntigravityQuotaStatus.Ready)]
    [InlineData(AntigravityQuotaStatus.NoQuota)]
    [InlineData(AntigravityQuotaStatus.Unavailable)]
    [InlineData(AntigravityQuotaStatus.ModelInvoked)]
    public async Task ABlockedVersionSurvivesQueryingAnotherVersionAcrossRestarts(AntigravityQuotaStatus secondStatus)
    {
        using var guard = new TemporaryGuard();
        var usageCalls = 0;
        var version = "1.5.18";
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken _)
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, version));
            return Task.FromResult(UsageResult(++usageCalls == 1 ? AntigravityQuotaStatus.ModelInvoked : secondStatus));
        }

        Task<AntigravityQuotaSnapshot> ReadAfterRestart() =>
            new AntigravityQuotaReader(() => "agy.exe", Run, new ManualClock(), guard.FilePath).ReadAsync();

        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await ReadAfterRestart()).Status);
        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await ReadAfterRestart()).Status);
        Assert.Equal(1, usageCalls);

        version = "1.5.19";
        Assert.Equal(secondStatus, (await ReadAfterRestart()).Status);
        version = "1.5.18";

        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await ReadAfterRestart()).Status);
        Assert.Equal(2, usageCalls);
    }

    [Fact]
    public async Task AVersionOneGuardIsMigratedAndStillBlocksTheSavedVersion()
    {
        using var guard = new TemporaryGuard();
        Directory.CreateDirectory(Path.GetDirectoryName(guard.FilePath)!);
        await File.WriteAllTextAsync(guard.FilePath, """{"version":1,"modelInvokingCliVersion":"1.5.18"}""");
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken _)
        {
            Assert.Equal(["--version"], info.ArgumentList);
            return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
        }

        Task<AntigravityQuotaSnapshot> ReadAfterRestart() =>
            new AntigravityQuotaReader(() => "agy.exe", Run, new ManualClock(), guard.FilePath).ReadAsync();

        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await ReadAfterRestart()).Status);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(guard.FilePath));
        Assert.Equal(2, saved.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("1.5.18", Assert.Single(saved.RootElement.GetProperty("blockedCliVersions").EnumerateArray()).GetString());
        Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await ReadAfterRestart()).Status);
    }

    [Theory]
    [InlineData(AntigravityQuotaStatus.Ready)]
    [InlineData(AntigravityQuotaStatus.NoQuota)]
    [InlineData(AntigravityQuotaStatus.Unavailable)]
    public async Task CooldownSurvivesRestartsEscalatesToTheCapAndClearsOnZeroTurns(AntigravityQuotaStatus verifiedStatus)
    {
        using var guard = new TemporaryGuard();
        var clock = new ManualClock();
        var usageCalls = 0;
        var verified = false;
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken _)
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
            usageCalls++;
            if (!verified) throw new InvalidDataException("CLI output exceeded its size limit.");
            return Task.FromResult(UsageResult(verifiedStatus));
        }

        Task<AntigravityQuotaSnapshot> ReadAfterRestart() =>
            new AntigravityQuotaReader(() => "agy.exe", Run, clock, guard.FilePath).ReadAsync();

        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
        Assert.Equal(1, usageCalls);

        TimeSpan[] waits = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6),
            TimeSpan.FromHours(24), TimeSpan.FromHours(24)];
        for (var index = 0; index < waits.Length; index++)
        {
            clock.Advance(waits[index] - TimeSpan.FromMinutes(1));
            Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
            Assert.Equal(index + 1, usageCalls);

            clock.Advance(TimeSpan.FromMinutes(1));
            verified = index == waits.Length - 1;
            Assert.Equal(verified ? verifiedStatus : AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
            Assert.Equal(index + 2, usageCalls);
        }

        // Another failure after a verified response starts at the first cooldown again.
        verified = false;
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
        clock.Advance(TimeSpan.FromMinutes(30));
        verified = true;
        Assert.Equal(verifiedStatus, (await ReadAfterRestart()).Status);
        Assert.Equal(waits.Length + 3, usageCalls);
    }

    [Theory]
    [InlineData(0, "not json")]
    [InlineData(0, "[]")]
    [InlineData(0, """{"num_turns":1,"num_turns":0}""")]
    [InlineData(1, """{"status":"ERROR"}""")]
    [InlineData(0, """{"status":"SUCCESS","command":{"name":"usage","data":{"groups":[]}}}""")]
    [InlineData(0, """{"num_turns":"0"}""")]
    public async Task OutputWithoutVerifiedZeroTurnsStartsACooldown(int exitCode, string output)
    {
        using var guard = new TemporaryGuard();
        var clock = new ManualClock();
        var usageCalls = 0;
        var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
            usageCalls++;
            return Task.FromResult(new ClaudeCliAuthCommandResult(exitCode, output));
        }, clock, guard.FilePath);

        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        Assert.Equal(1, usageCalls);
        Assert.True(File.Exists(guard.FilePath));
    }

    [Fact]
    public async Task CooldownIsPerVersionAndAClockRollbackCapsTheWait()
    {
        using var guard = new TemporaryGuard();
        var futureClock = new ManualClock();
        futureClock.Advance(TimeSpan.FromDays(7));
        var version = "1.5.18";
        var usageCalls = 0;
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken _)
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, version));
            if (++usageCalls == 1) throw new DecoderFallbackException("Invalid UTF-8.");
            return Task.FromResult(UsageResult(AntigravityQuotaStatus.NoQuota));
        }

        Assert.Equal(AntigravityQuotaStatus.Unavailable,
            (await new AntigravityQuotaReader(() => "agy.exe", Run, futureClock, guard.FilePath).ReadAsync()).Status);
        var clock = new ManualClock();
        var reader = new AntigravityQuotaReader(() => "agy.exe", Run, clock, guard.FilePath);
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        Assert.Equal(1, usageCalls);

        version = "1.5.19";
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AntigravityQuotaStatus.NoQuota, (await reader.ReadAsync()).Status);

        version = "1.5.18";
        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(58));
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        Assert.Equal(2, usageCalls);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AntigravityQuotaStatus.NoQuota, (await reader.ReadAsync()).Status);
        Assert.Equal(3, usageCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EachGuardListRetainsTheEightMostRecentVersions(bool modelInvoked)
    {
        using var guard = new TemporaryGuard();
        var clock = new ManualClock();
        var status = modelInvoked ? AntigravityQuotaStatus.ModelInvoked : AntigravityQuotaStatus.Unavailable;
        var version = "1.5.11";
        var usageCalls = 0;
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken _)
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, version));
            usageCalls++;
            return Task.FromResult(modelInvoked ? UsageResult(status) : new ClaudeCliAuthCommandResult(1, ""));
        }

        Task<AntigravityQuotaSnapshot> ReadAfterRestart() =>
            new AntigravityQuotaReader(() => "agy.exe", Run, clock, guard.FilePath).ReadAsync();

        for (var patch = 11; patch <= 19; patch++)
        {
            version = $"1.5.{patch}";
            Assert.Equal(status, (await ReadAfterRestart()).Status);
        }

        version = "1.5.12";
        Assert.Equal(status, (await ReadAfterRestart()).Status);
        Assert.Equal(9, usageCalls);
        version = "1.5.11";
        Assert.Equal(status, (await ReadAfterRestart()).Status);
        Assert.Equal(10, usageCalls);
    }

    [Fact]
    public async Task VersionCheckFailuresDoNotStartACooldown()
    {
        using var guard = new TemporaryGuard();
        var clock = new ManualClock();
        var versionCalls = 0;
        var usageCalls = 0;
        var reader = new AntigravityQuotaReader(() => "agy.exe", (info, token) =>
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
            {
                if (++versionCalls == 1) return Task.FromResult(new ClaudeCliAuthCommandResult(1, "1.5.18"));
                if (versionCalls == 2)
                {
                    clock.Advance(TimeSpan.FromSeconds(18));
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
            }
            usageCalls++;
            return Task.FromResult(UsageResult(AntigravityQuotaStatus.NoQuota));
        }, clock, guard.FilePath);

        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);
        Assert.Equal(0, usageCalls);
        Assert.False(File.Exists(guard.FilePath));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(AntigravityQuotaStatus.NoQuota, (await reader.ReadAsync()).Status);
        Assert.Equal(1, usageCalls);
        Assert.False(File.Exists(guard.FilePath));
    }

    [Fact]
    public async Task UsageHasAnIndependentDeadlineAndItsExpiryStartsACooldown()
    {
        using var guard = new TemporaryGuard();
        var clock = new ManualClock();
        var usageCalls = 0;
        Task<ClaudeCliAuthCommandResult> Run(ProcessStartInfo info, CancellationToken token)
        {
            if (info.ArgumentList.SequenceEqual(["--version"]))
            {
                clock.Advance(TimeSpan.FromSeconds(17));
                Assert.False(token.IsCancellationRequested);
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
            }

            usageCalls++;
            clock.Advance(TimeSpan.FromSeconds(17));
            Assert.False(token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(token.IsCancellationRequested);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(UsageResult(AntigravityQuotaStatus.NoQuota));
        }

        Task<AntigravityQuotaSnapshot> ReadAfterRestart() =>
            new AntigravityQuotaReader(() => "agy.exe", Run, clock, guard.FilePath).ReadAsync();

        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
        Assert.Equal(AntigravityQuotaStatus.Unavailable, (await ReadAfterRestart()).Status);
        Assert.Equal(1, usageCalls);
    }

    [Fact]
    public async Task ALockedGuardFileSkipsTheQueryUntilItCanBeRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-agy-guard-" + Guid.NewGuid().ToString("N"));
        var guard = Path.Combine(root, "antigravity", "quota-guard.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guard)!);
            await File.WriteAllTextAsync(guard, """{"version":1,"modelInvokingCliVersion":"1.5.18"}""");
            var usageCalls = 0;
            var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
            {
                if (info.ArgumentList.SequenceEqual(["--version"]))
                    return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
                usageCalls++;
                return Task.FromResult(new ClaudeCliAuthCommandResult(3, """{"status":"ERROR","num_turns":1}"""));
            }, new ManualClock(), guard);

            // Another program holds the file: the blocked version must not run.
            using (new FileStream(guard, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(AntigravityQuotaStatus.Unavailable, (await reader.ReadAsync()).Status);

            Assert.Equal(AntigravityQuotaStatus.ModelInvoked, (await reader.ReadAsync()).Status);
            Assert.Equal(0, usageCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":1,"modelInvokingCliVersion":"not a version"}""")]
    [InlineData("""{"version":9,"modelInvokingCliVersion":"1.5.18"}""")]
    public async Task AnUnusableGuardFileIsIgnored(string saved)
    {
        var root = Path.Combine(Path.GetTempPath(), "codexu-agy-guard-" + Guid.NewGuid().ToString("N"));
        var guard = Path.Combine(root, "antigravity", "quota-guard.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guard)!);
            await File.WriteAllTextAsync(guard, saved);
            var usageCalls = 0;
            var reader = new AntigravityQuotaReader(() => "agy.exe", (info, _) =>
            {
                if (info.ArgumentList.SequenceEqual(["--version"]))
                    return Task.FromResult(new ClaudeCliAuthCommandResult(0, "1.5.18"));
                usageCalls++;
                return Task.FromResult(new ClaudeCliAuthCommandResult(0, Envelope("""[{"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":1}]}]""")));
            }, new ManualClock(), guard);

            Assert.Equal(AntigravityQuotaStatus.Ready, (await reader.ReadAsync()).Status);
            Assert.Equal(1, usageCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Envelope(string groups, string status = "SUCCESS", string command = "usage", int turns = 0) =>
        $$"""{"status":"{{status}}","num_turns":{{turns}},"command":{"name":"{{command}}","data":{"groups":{{groups}} } } }""";

    private static ClaudeCliAuthCommandResult UsageResult(AntigravityQuotaStatus status) => status switch
    {
        AntigravityQuotaStatus.Ready => new(0, Envelope("""[{"name":"Gemini","buckets":[{"window":"5h","remaining_fraction":1}]}]""")),
        AntigravityQuotaStatus.NoQuota => new(0, Envelope("[]")),
        AntigravityQuotaStatus.Unavailable => new(1, """{"status":"ERROR","num_turns":0}"""),
        AntigravityQuotaStatus.ModelInvoked => new(3, """{"status":"ERROR","num_turns":1}"""),
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private sealed class TemporaryGuard : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "codexu-agy-guard-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(_root, "antigravity", "quota-guard.json");

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly List<DeadlineTimer> _timers = [];
        private TimeSpan _elapsed;
        public override DateTimeOffset GetUtcNow() => Now + _elapsed;
        public override long GetTimestamp() => _elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new DeadlineTimer(_elapsed + dueTime, callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers) timer.Tick(_elapsed);
        }

        private sealed class DeadlineTimer(TimeSpan dueAt, TimerCallback callback, object? state) : ITimer
        {
            private bool _active = true;

            public void Tick(TimeSpan elapsed)
            {
                if (!_active || elapsed < dueAt) return;
                _active = false;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => _active = false;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
