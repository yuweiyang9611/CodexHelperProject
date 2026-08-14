using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexSessionUsageDeduplicationTests
{
    [Fact]
    public async Task ParseFileAsync_DoesNotCountRepeatedLastUsageWhenCumulativeDidNotAdvance()
    {
        var path = TemporaryJsonlPath();
        try
        {
            var cumulative = new Usage(100, 60, 20, 5, 120);
            var last = new Usage(100, 60, 20, 5, 120);
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, cumulative, last),
                TokenLine(2, cumulative, last)
            ]);

            var parsed = await CodexSessionReader.ParseFileAsync(path);
            var bucket = Assert.Single(parsed.UsageBuckets);

            Assert.Equal(120, bucket.Tokens.TotalTokens);
            Assert.Equal(1, bucket.EventCount);
            // TokenEventCount intentionally describes parse coverage, including a
            // syntactically valid duplicate notification that contributes no usage.
            Assert.Equal(2, parsed.TokenEventCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_UsesLastUsageInsteadOfNewCumulativeBaselineAfterReset()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                // The process counter restarted and already contains more than the
                // latest response. Only last_token_usage is newly attributable.
                TokenLine(2, new Usage(30, 12, 10, 2, 40), new Usage(12, 5, 3, 1, 15))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(115, total.TotalTokens);
            Assert.Equal(92, total.InputTokens);
            Assert.Equal(45, total.CachedInputTokens);
            Assert.Equal(23, total.OutputTokens);
            Assert.Equal(5, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_UsesResetBaselineWhenLastUsageIsMissing()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(16, 8, 4, 1, 20)),
                TokenLine(3, new Usage(36, 15, 9, 2, 45))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            // Without a per-response sample, the new cumulative epoch is the only
            // available local evidence: 100 before reset + 20 baseline + 25 advance.
            Assert.Equal(145, total.TotalTokens);
            Assert.Equal(116, total.InputTokens);
            Assert.Equal(55, total.CachedInputTokens);
            Assert.Equal(29, total.OutputTokens);
            Assert.Equal(6, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_UsesLastUsageWhenAuxiliaryCumulativeFieldsRegress()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(100, 80, 20, 10, 120), new Usage(100, 80, 20, 10, 120)),
                // Total and the primary input/output counters advance, while cached
                // and reasoning counters fall. The complete last usage preserves the
                // real per-response breakdown without treating either fall as reset.
                TokenLine(2, new Usage(160, 50, 40, 5, 200), new Usage(60, 30, 20, 4, 80)),
                // Both auxiliary counters recover but remain below their original
                // high-water. They must not be counted again without last usage.
                TokenLine(3, new Usage(180, 70, 50, 8, 230))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(230, total.TotalTokens);
            Assert.Equal(180, total.InputTokens);
            Assert.Equal(110, total.CachedInputTokens);
            Assert.Equal(50, total.OutputTokens);
            Assert.Equal(14, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_DoesNotTreatMalformedTotalRollbackAsEpochReset()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(100, 80, 20, 10, 120), new Usage(100, 80, 20, 10, 120)),
                // The total field regresses, but both primary cumulative slices
                // advance. This is a malformed auxiliary field, not a new epoch.
                TokenLine(2, new Usage(160, 90, 40, 12, 80), new Usage(60, 10, 20, 2, 80)),
                // This total is above the malformed 80 but below the real 120
                // high-water. A false reset would count another 20 here.
                TokenLine(3, new Usage(170, 95, 45, 13, 100))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(200, total.TotalTokens);
            Assert.Equal(170, total.InputTokens);
            Assert.Equal(95, total.CachedInputTokens);
            Assert.Equal(45, total.OutputTokens);
            Assert.Equal(13, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_FallsBackPerFieldWhenLastUsageIsNegative()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(100, 80, 20, 10, 120), new Usage(100, 80, 20, 10, 120)),
                TokenLine(2, new Usage(160, 95, 40, 14, 200), new Usage(60, -1, 20, -1, 80))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(200, total.TotalTokens);
            Assert.Equal(160, total.InputTokens);
            Assert.Equal(95, total.CachedInputTokens);
            Assert.Equal(40, total.OutputTokens);
            Assert.Equal(14, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_InvalidCumulativeDoesNotPoisonHighWater()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(-1, -1, -1, -1, -1), new Usage(15, 5, 5, 1, 20)),
                TokenLine(3, new Usage(120, 55, 30, 7, 150))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            // 100 + valid last-only 20 + cumulative advance 50. The invalid
            // sample must not lower state and turn the final 150 into a baseline.
            Assert.Equal(170, total.TotalTokens);
            Assert.Equal(135, total.InputTokens);
            Assert.Equal(60, total.CachedInputTokens);
            Assert.Equal(35, total.OutputTokens);
            Assert.Equal(8, total.ReasoningOutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_CountsLastOnlyUsageWithoutCumulative()
    {
        var path = TemporaryJsonlPath();
        try
        {
            await File.WriteAllTextAsync(
                path,
                LastOnlyTokenLine(1, new Usage(45, 15, 15, 3, 60)));

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(60, total.TotalTokens);
            Assert.Equal(45, total.InputTokens);
            Assert.Equal(15, total.OutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_NewCumulativeFieldsDoNotReplayUnchangedTotal()
    {
        var path = TemporaryJsonlPath();
        try
        {
            var first = JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-11T01:00:01Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new { total_tokens = 100L },
                        last_token_usage = new { total_tokens = 100L }
                    }
                }
            });
            var repeatedWithNewFields = JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-11T01:00:02Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new { input_tokens = 80L, output_tokens = 20L, total_tokens = 100L },
                        last_token_usage = new { input_tokens = 80L, output_tokens = 20L, total_tokens = 100L }
                    }
                }
            });
            await File.WriteAllLinesAsync(path, [first, repeatedWithNewFields]);

            var parsed = await CodexSessionReader.ParseFileAsync(path);
            var total = Total(parsed);

            Assert.Equal(100, total.TotalTokens);
            Assert.Equal(1, Assert.Single(parsed.UsageBuckets).EventCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_TotalDropWithoutInputDoesNotConfirmReset()
    {
        var path = TemporaryJsonlPath();
        try
        {
            var unconfirmedDrop = JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-11T01:00:02Z",
                type = "event_msg",
                payload = new
                {
                    type = "token_count",
                    info = new
                    {
                        total_token_usage = new { total_tokens = 80L },
                        last_token_usage = new { total_tokens = 20L }
                    }
                }
            });
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(100, 80, 20, 10, 120), new Usage(100, 80, 20, 10, 120)),
                unconfirmedDrop,
                TokenLine(3, new Usage(120, 95, 30, 13, 150), new Usage(20, 15, 10, 3, 30))
            ]);

            var total = Total(await CodexSessionReader.ParseFileAsync(path));

            Assert.Equal(150, total.TotalTokens);
            Assert.Equal(120, total.InputTokens);
            Assert.Equal(95, total.CachedInputTokens);
            Assert.Equal(30, total.OutputTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_PersistsNormalizerStateAcrossIncrementalContinuation()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-incremental-normalizer.jsonl");
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(path,
            [
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);

            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            Assert.Equal(160, first.Tokens.Lifetime.Tokens);

            await File.AppendAllTextAsync(
                path,
                TokenLine(3, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
                + Environment.NewLine
                + TokenLine(4, new Usage(155, 65, 45, 9, 200), new Usage(30, 10, 10, 2, 40))
                + Environment.NewLine);

            var continued = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, continued.IndexStatus.IncrementalFiles);
            Assert.Equal(200, continued.Tokens.Lifetime.Tokens);
            Assert.Equal(3, continued.Models.Single().EventCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ManualForkDropsOnlyLongestCommonTokenPrefix()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-parent.jsonl"),
            [
                SessionMetaLine("parent"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60)),
                TokenLine(3, new Usage(170, 70, 50, 10, 220), new Usage(45, 15, 15, 3, 60))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-child.jsonl"),
            [
                SessionMetaLine("child", forkedFromId: "parent"),
                TokenLine(11, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(12, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60)),
                TokenLine(13, new Usage(175, 75, 55, 11, 230), new Usage(50, 20, 20, 4, 70))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            // Parent A+B+C (220) + only the child's post-fork D (70).
            Assert.Equal(290, result.Tokens.Lifetime.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReusesPersistedTokenEventsForForkPrefix()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-parent.jsonl"),
            [
                SessionMetaLine("parent"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-child.jsonl"),
            [
                SessionMetaLine("child", forkedFromId: "parent"),
                TokenLine(11, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(12, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60)),
                TokenLine(13, new Usage(175, 75, 55, 11, 230), new Usage(50, 20, 20, 4, 70))
            ]);

            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            var cached = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(230, first.Tokens.Lifetime.Tokens);
            Assert.Equal(230, cached.Tokens.Lifetime.Tokens);
            Assert.Equal(2, cached.IndexStatus.ReusedFiles);
            Assert.Contains(cached.Diagnostics, item => item.Contains("父子公共前缀 1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ThreadSpawnBoundaryTakesPrecedenceOverMatchingParentPrefix()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            var a = TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100));
            var b = TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60));
            var c = TokenLine(3, new Usage(170, 70, 50, 10, 220), new Usage(45, 15, 15, 3, 60));
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-parent.jsonl"),
            [
                SessionMetaLine("parent"),
                a,
                b,
                c
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-child.jsonl"),
            [
                ThreadSpawnMetaLine("child", "parent"),
                SessionMetaLine("parent"),
                WithSecond(a, 11),
                WithSecond(b, 12),
                InterAgentBoundaryLine(),
                // This event has the same usage fingerprint as the parent's C, but
                // it occurs after the explicit boundary and is real child work.
                WithSecond(c, 13)
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(280, result.Tokens.Lifetime.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_IncompleteThreadSpawnUsesParentThreadIdForPrefixFallback()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-parent.jsonl"),
            [
                SessionMetaLine("parent"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-child.jsonl"),
            [
                ThreadSpawnParentOnlyMetaLine("child", "parent"),
                TokenLine(11, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(12, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60)),
                TokenLine(13, new Usage(175, 75, 55, 11, 230), new Usage(50, 20, 20, 4, 70))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(230, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Detailed, result.Tokens.Lifetime.Quality);
            Assert.Contains(result.Diagnostics, item => item.Contains("父子公共前缀 1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_IncompleteStructuralBoundaryLetsPrefixPreserveChildTail()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            var a = TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100));
            var b = TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60));
            var childTail = TokenLine(13, new Usage(175, 75, 55, 11, 230), new Usage(50, 20, 20, 4, 70));
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-parent.jsonl"),
            [
                SessionMetaLine("parent"),
                a,
                b
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-child.jsonl"),
            [
                ThreadSpawnMetaLine("child", "parent"),
                SessionMetaLine("parent"),
                WithSecond(a, 11),
                WithSecond(b, 12),
                // Simulate a truncated/missing structural marker. LCP can still
                // prove A+B are inherited and must retain the divergent child tail.
                childTail
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(230, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Detailed, result.Tokens.Lifetime.Quality);
            Assert.Contains(result.Diagnostics, item => item.Contains("父子公共前缀 1", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MissingForkParentKeepsChildUsageAndMarksItAmbiguous()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-orphan-child.jsonl"),
            [
                SessionMetaLine("child", forkedFromId: "missing-parent"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(160, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Contains(
                result.Diagnostics,
                item => item.Contains("fork", StringComparison.OrdinalIgnoreCase)
                        || item.Contains("父", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReplayingWithoutParentKeepsConservativeUpperBound()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-orphan-spawn.jsonl"),
            [
                ThreadSpawnMetaLine("child", "missing-parent"),
                SessionMetaLine("missing-parent"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(160, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Contains(result.Diagnostics, item => item.Contains("160", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MergesActiveAndArchivedCopiesOfSameSessionWithoutDoubleCounting()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        var archived = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-active.jsonl"),
            [
                SessionMetaLine("same-session"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(archived, "archived-copy.jsonl"),
            [
                SessionMetaLine("same-session"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100)),
                TokenLine(2, new Usage(125, 55, 35, 7, 160), new Usage(45, 15, 15, 3, 60))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(160, result.Tokens.Lifetime.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DivergentCopiesChooseOneAndMarkPartial()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        var archived = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archived);
        var paths = CreatePaths(root);

        try
        {
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-active.jsonl"),
            [
                SessionMetaLine("same-session"),
                TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100))
            ]);
            await File.WriteAllLinesAsync(Path.Combine(archived, "archived-divergent.jsonl"),
            [
                SessionMetaLine("same-session"),
                TokenLine(1, new Usage(70, 30, 20, 3, 90), new Usage(70, 30, 20, 3, 90)),
                TokenLine(2, new Usage(125, 50, 35, 7, 160), new Usage(55, 20, 15, 4, 70))
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(160, result.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Contains(result.Diagnostics, item => item.Contains("内容分叉", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotGloballyDeduplicateUnrelatedSessionsWithIdenticalUsage()
    {
        var root = TemporaryRoot();
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var paths = CreatePaths(root);

        try
        {
            var usage = TokenLine(1, new Usage(80, 40, 20, 4, 100), new Usage(80, 40, 20, 4, 100));
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-one.jsonl"),
            [
                SessionMetaLine("independent-one"),
                usage
            ]);
            await File.WriteAllLinesAsync(Path.Combine(sessions, "rollout-two.jsonl"),
            [
                SessionMetaLine("independent-two"),
                WithSecond(usage, 11)
            ]);

            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(200, result.Tokens.Lifetime.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TokenBreakdown Total(ParsedSessionFile parsed) => parsed.UsageBuckets.Aggregate(
        TokenBreakdown.Zero,
        (current, bucket) => current.Add(bucket.Tokens));

    private static CodexPaths CreatePaths(string root) => new(
        root,
        root,
        Path.Combine(root, "missing.db"),
        Path.Combine(root, "sessions"),
        Path.Combine(root, "archived_sessions"),
        Path.Combine(root, "automations"),
        Path.Combine(root, "missing-goals.db"),
        Path.Combine(root, "missing-claude"));

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"codexu-token-dedupe-{Guid.NewGuid():N}");

    private static string TemporaryJsonlPath() =>
        Path.Combine(Path.GetTempPath(), $"codexu-token-dedupe-{Guid.NewGuid():N}.jsonl");

    private static string SessionMetaLine(string id, string? forkedFromId = null) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-07-11T01:00:00Z",
        type = "session_meta",
        payload = new
        {
            id,
            forked_from_id = forkedFromId
        }
    });

    private static string ThreadSpawnMetaLine(string id, string parentId) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-07-11T01:00:00Z",
        type = "session_meta",
        payload = new
        {
            id,
            parent_thread_id = parentId,
            forked_from_id = parentId,
            thread_source = "subagent",
            source = new
            {
                subagent = new
                {
                    thread_spawn = new { }
                }
            }
        }
    });

    private static string ThreadSpawnParentOnlyMetaLine(string id, string parentId) => JsonSerializer.Serialize(new
    {
        timestamp = "2026-07-11T01:00:00Z",
        type = "session_meta",
        payload = new
        {
            id,
            parent_thread_id = parentId,
            thread_source = "subagent",
            source = new
            {
                subagent = new
                {
                    thread_spawn = new { }
                }
            }
        }
    });

    private static string InterAgentBoundaryLine() => JsonSerializer.Serialize(new
    {
        timestamp = "2026-07-11T01:00:12Z",
        type = "inter_agent_communication_metadata",
        payload = new { trigger_turn = true }
    });

    private static string TokenLine(int second, Usage cumulative, Usage? last = null)
    {
        var info = new Dictionary<string, object?>
        {
            ["total_token_usage"] = UsageObject(cumulative)
        };
        if (last is not null)
        {
            info["last_token_usage"] = UsageObject(last);
        }

        return JsonSerializer.Serialize(new
        {
            timestamp = $"2026-07-11T01:00:{second:00}Z",
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info
            }
        });
    }

    private static string LastOnlyTokenLine(int second, Usage last) => JsonSerializer.Serialize(new
    {
        timestamp = $"2026-07-11T01:00:{second:00}Z",
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                last_token_usage = UsageObject(last)
            }
        }
    });

    private static object UsageObject(Usage usage) => new
    {
        input_tokens = usage.Input,
        cached_input_tokens = usage.CachedInput,
        output_tokens = usage.Output,
        reasoning_output_tokens = usage.Reasoning,
        total_tokens = usage.Total
    };

    private static string WithSecond(string json, int second)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var payload = root.GetProperty("payload").Clone();
        return JsonSerializer.Serialize(new
        {
            timestamp = $"2026-07-11T01:00:{second:00}Z",
            type = root.GetProperty("type").GetString(),
            payload
        });
    }

    private sealed record Usage(long Input, long CachedInput, long Output, long Reasoning, long Total);
}
