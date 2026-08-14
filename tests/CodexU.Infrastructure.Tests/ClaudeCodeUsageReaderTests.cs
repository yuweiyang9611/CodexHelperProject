using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeCodeUsageReaderTests
{
    [Fact]
    public async Task ReadAsync_DrainsOversizedTranscriptLineAndContinuesWithFollowingUsage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-bounded-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var valid = JsonSerializer.Serialize(new
            {
                type = "assistant",
                cwd = root,
                timestamp = DateTimeOffset.Now.ToString("O"),
                message = new
                {
                    model = "claude-sonnet-4-5",
                    usage = new { input_tokens = 80, output_tokens = 20 }
                }
            });
            await File.WriteAllTextAsync(
                Path.Combine(projectDirectory, "session.jsonl"),
                new string('x', BoundedLineReader.DefaultMaximumLineBytes + 1)
                + Environment.NewLine
                + valid
                + Environment.NewLine);

            var result = await new ClaudeCodeUsageReader(CreatePaths(root, claude)).ReadAsync();

            Assert.Equal(100, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Contains(result.Diagnostics, value => value.Contains("单行超过 4 MiB", StringComparison.Ordinal));
            Assert.Contains(result.Diagnostics, value => value.Contains("1 行无效或过大", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_AggregatesSelectedWorkspaceAndSkipsMalformedLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        var selectedWorkspace = Path.Combine(root, "selected");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(selectedWorkspace);
        try
        {
            var timestamp = DateTimeOffset.Now.ToString("O");
            var excluded = JsonSerializer.Serialize(new
            {
                type = "assistant",
                cwd = Path.Combine(root, "excluded"),
                timestamp,
                message = new
                {
                    model = "claude-sonnet-4-5",
                    usage = new { input_tokens = 10_000, cache_read_input_tokens = 0, output_tokens = 10_000 },
                    content = new[] { new { type = "tool_use", name = "Bash", input = new { skill = "ignored" } } }
                }
            });
            var included = JsonSerializer.Serialize(new
            {
                type = "assistant",
                cwd = selectedWorkspace,
                timestamp,
                message = new
                {
                    model = "claude-sonnet-4-5",
                    usage = new
                    {
                        input_tokens = 100,
                        cache_creation_input_tokens = 20,
                        cache_read_input_tokens = 30,
                        output_tokens = 50
                    },
                    content = new[] { new { type = "tool_use", name = "Skill", input = new { skill = "frontend-design" } } }
                }
            });
            await File.WriteAllLinesAsync(
                Path.Combine(projectDirectory, "session.jsonl"),
                [excluded, "{ malformed", included]);

            var paths = CreatePaths(root, claude);
            var rates = new[]
            {
                new ModelCreditRate("claude-sonnet", 100, 10, 200, MatchMode: "prefix")
            };
            var result = await new ClaudeCodeUsageReader(paths, selectedWorkspace, customRates: rates).ReadAsync();

            Assert.Equal(200, result.Tokens.Lifetime.Tokens);
            Assert.Equal(150, result.Tokens.Lifetime.Breakdown.InputTokens);
            Assert.Equal(30, result.Tokens.Lifetime.Breakdown.CachedInputTokens);
            Assert.Equal(50, result.Tokens.Lifetime.Breakdown.OutputTokens);
            // No nested cache_creation object here, so the flat counter's 20 tokens
            // fall to the cheaper 5 minute tier rather than being priced as plain input.
            Assert.Equal(20, result.Tokens.Lifetime.Breakdown.BillableCacheWrite5mTokens);
            Assert.Equal(0, result.Tokens.Lifetime.Breakdown.BillableCacheWrite1hTokens);
            Assert.Equal(100, result.Tokens.Lifetime.Breakdown.UncachedInputTokens);
            // 100 uncached @100 + 30 cached @10 + 20 writes @100*1.25 + 50 output @200.
            Assert.Equal(0.0228d, result.Tokens.Lifetime.CreditsUsed, precision: 6);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Equal("Skill", Assert.Single(result.Tools).Name);
            Assert.Equal("frontend-design", Assert.Single(result.Skills).Name);
            Assert.Equal(Path.GetFullPath(selectedWorkspace), Assert.Single(result.Projects).FullPath);
            Assert.Contains(result.Diagnostics, value => value.Contains("无效行", StringComparison.Ordinal));
            Assert.Equal(1, result.IndexStatus.ParsedFiles);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_PinnedCatalogDoesNotFallBackToBuiltInRates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-pinned-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var transcript = JsonSerializer.Serialize(new
            {
                type = "assistant",
                cwd = root,
                timestamp = DateTimeOffset.Now.ToString("O"),
                message = new
                {
                    model = "gpt-5.6-sol",
                    usage = new { input_tokens = 100, output_tokens = 20 }
                }
            });
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "session.jsonl"), transcript);
            var pinnedRates = new[]
            {
                new ModelCreditRate("archive-model", 10, 1, 100)
            };

            var result = await new ClaudeCodeUsageReader(
                CreatePaths(root, claude),
                customRates: pinnedRates,
                completeRateCatalog: true).ReadAsync();

            Assert.Equal(0, result.Tokens.Lifetime.CreditsUsed);
            Assert.Equal(120, result.Tokens.Lifetime.UnratedTokens);
            Assert.Empty(result.Tokens.Lifetime.CreditsByModel);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_FiltersStatuslineQuotaWithoutResetAfterSnapshotWindowExpires()
    {
        var result = await ReadQuotaSnapshotAsync(
            DateTimeOffset.UtcNow.AddHours(-6),
            resetsAt: null,
            windowMinutes: 300);

        Assert.Null(result.PrimaryQuota);
        Assert.Contains(result.Diagnostics, value => value.Contains("已忽略 1 个过期额度窗口", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_KeepsStatuslineQuotaWithoutResetWhileSnapshotWindowIsCurrent()
    {
        var result = await ReadQuotaSnapshotAsync(
            DateTimeOffset.UtcNow.AddMinutes(-30),
            resetsAt: null,
            windowMinutes: 300);

        Assert.Equal(38, result.PrimaryQuota?.UsedPercent);
    }

    [Fact]
    public async Task ReadAsync_UsesStatuslineExplicitResetInsteadOfOldSnapshotTimestamp()
    {
        var result = await ReadQuotaSnapshotAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddHours(2),
            windowMinutes: 300);

        Assert.Equal(38, result.PrimaryQuota?.UsedPercent);
    }

    [Fact]
    public async Task ReadAsync_PricesCacheWritesByTheirTtlTier()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-ttl-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            // Real transcripts carry the per-TTL split under cache_creation; the flat
            // cache_creation_input_tokens counter stays authoritative for the total.
            var line = JsonSerializer.Serialize(new
            {
                type = "assistant",
                cwd = root,
                timestamp = DateTimeOffset.Now.ToString("O"),
                message = new
                {
                    model = "claude-opus-5",
                    usage = new
                    {
                        input_tokens = 0,
                        cache_creation_input_tokens = 3_000_000,
                        cache_read_input_tokens = 0,
                        output_tokens = 0,
                        cache_creation = new
                        {
                            ephemeral_5m_input_tokens = 1_000_000,
                            ephemeral_1h_input_tokens = 2_000_000
                        }
                    }
                }
            });
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "session.jsonl"), line);

            var rates = new[] { new ModelCreditRate("claude-opus-5", 100, 10, 200) };
            var result = await new ClaudeCodeUsageReader(CreatePaths(root, claude), customRates: rates).ReadAsync();
            var breakdown = result.Tokens.Lifetime.Breakdown;

            Assert.Equal(1_000_000, breakdown.BillableCacheWrite5mTokens);
            Assert.Equal(2_000_000, breakdown.BillableCacheWrite1hTokens);
            Assert.Equal(0, breakdown.UncachedInputTokens);
            // 1M at 1.25x plus 2M at 2x of the 100/M base input rate. Billing all 3M as
            // plain input would have charged 300 — a 58% under-report.
            Assert.Equal(525, result.Tokens.Lifetime.CreditsUsed, precision: 6);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ConsumesTheSnapshotShapeTheStatuslineCollectorWrites()
    {
        // Contract test between tools/claude-statusline-snapshot.mjs and this reader.
        // The collector is the only documented way to get Claude quota onto disk —
        // Claude Code itself persists none — so if the two ever disagree the rings
        // silently stay empty. This is the collector's exact output, byte for byte.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-contract-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        Directory.CreateDirectory(claude);
        var snapshotPath = Path.Combine(claude, "statusline-snapshot.json");
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        await File.WriteAllTextAsync(snapshotPath, $$"""
        {
          "source": "claude-code-statusline",
          "capturedAt": "{{DateTimeOffset.UtcNow:O}}",
          "primary": {
            "usedPercent": 23.5,
            "windowDurationMinutes": 300,
            "resetsAt": "{{resetsAt:O}}"
          },
          "secondary": {
            "usedPercent": 41.2,
            "windowDurationMinutes": 10080,
            "resetsAt": "{{resetsAt.AddDays(5):O}}"
          }
        }
        """);

        try
        {
            var result = await new ClaudeCodeUsageReader(CreatePaths(root, claude)).ReadAsync();

            Assert.NotNull(result.PrimaryQuota);
            Assert.Equal(23.5, result.PrimaryQuota.UsedPercent, precision: 6);
            Assert.Equal(300, result.PrimaryQuota.WindowDurationMinutes);
            Assert.NotNull(result.SecondaryQuota);
            Assert.Equal(41.2, result.SecondaryQuota.UsedPercent, precision: 6);
            Assert.Equal(10080, result.SecondaryQuota.WindowDurationMinutes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DatesQuotaByWhenTheSnapshotWasWritten()
    {
        // The burn-rate forecast divides consumption by elapsed time. Claude Code
        // rewrites this file on its own schedule, so a refresh that re-reads an
        // unchanged file must not present it as a fresh measurement.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-measured-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        Directory.CreateDirectory(claude);
        var snapshotPath = Path.Combine(claude, "statusline-snapshot.json");
        var resetsAt = DateTimeOffset.UtcNow.AddHours(3);
        await File.WriteAllTextAsync(snapshotPath, $$"""
        {
          "primary": { "usedPercent": 23.5, "windowDurationMinutes": 300, "resetsAt": "{{resetsAt:O}}" }
        }
        """);
        var writtenAt = DateTimeOffset.UtcNow.AddMinutes(-12);
        File.SetLastWriteTimeUtc(snapshotPath, writtenAt.UtcDateTime);

        try
        {
            var result = await new ClaudeCodeUsageReader(CreatePaths(root, claude)).ReadAsync();

            Assert.NotNull(result.PrimaryQuota);
            Assert.NotNull(result.PrimaryQuota.MeasuredAt);
            Assert.Equal(writtenAt, result.PrimaryQuota.MeasuredAt.Value, TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CodexPaths CreatePaths(string root, string claude) => new(
        root,
        Path.Combine(root, ".codex"),
        Path.Combine(root, ".codex", "state.sqlite"),
        Path.Combine(root, ".codex", "sessions"),
        Path.Combine(root, ".codex", "archived_sessions"),
        Path.Combine(root, ".codex", "automations"),
        Path.Combine(root, ".codex", "goals.sqlite"),
        claude);

    private static async Task<LocalUsageSnapshot> ReadQuotaSnapshotAsync(
        DateTimeOffset snapshotTimestamp,
        DateTimeOffset? resetsAt,
        int windowMinutes)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-claude-quota-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        Directory.CreateDirectory(claude);
        var snapshotPath = Path.Combine(claude, "statusline-snapshot.json");
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(new
        {
            planType = "pro",
            primary = new
            {
                usedPercent = 38,
                windowDurationMinutes = windowMinutes,
                resetsAt = resetsAt?.ToString("O")
            }
        }));
        File.SetLastWriteTimeUtc(snapshotPath, snapshotTimestamp.UtcDateTime);

        try
        {
            return await new ClaudeCodeUsageReader(CreatePaths(root, claude)).ReadAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
