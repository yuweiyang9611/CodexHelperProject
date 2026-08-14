using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexSessionReaderTests
{
    [Fact]
    public async Task ReadAsync_SkipsOversizedAndMalformedCompleteLinesAndPreservesPartialQualityInCache()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-bounded-lines-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-bounded.jsonl");
        var validLine = CreateTokenLine(DateTimeOffset.Now.ToString("O"));
        await File.WriteAllTextAsync(
            path,
            new string('x', BoundedLineReader.DefaultMaximumLineBytes + 1)
            + Environment.NewLine
            + "{ malformed-but-complete"
            + Environment.NewLine
            + validLine
            + Environment.NewLine);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            var cached = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, first.TokenEventCount);
            Assert.Equal(100, first.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, first.Tokens.Lifetime.Quality);
            Assert.Contains(first.Diagnostics, item => item.Contains("2 行无效或超过 4 MiB", StringComparison.Ordinal));
            Assert.Equal(1, cached.IndexStatus.ReusedFiles);
            Assert.Equal(DataQuality.Partial, cached.Tokens.Lifetime.Quality);
            Assert.Contains(cached.Diagnostics, item => item.Contains("2 行无效或超过 4 MiB", StringComparison.Ordinal));

            var indexPath = Path.Combine(root, "session-index-v1.json");
            var legacyIndex = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
            legacyIndex["version"] = 4;
            legacyIndex["entries"]![0]!["parsed"]!.AsObject().Remove("skippedLineCount");
            await File.WriteAllTextAsync(indexPath, legacyIndex.ToJsonString());

            var afterLegacyCache = await new CodexSessionReader(paths, true, root).ReadAsync();
            Assert.Equal(1, afterLegacyCache.IndexStatus.ParsedFiles);
            Assert.Equal(DataQuality.Partial, afterLegacyCache.Tokens.Lifetime.Quality);
            Assert.Contains(afterLegacyCache.Diagnostics, item => item.Contains("2 行无效或超过 4 MiB", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileAsync_ComputesCumulativeDeltasAndCountsTools()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-{Guid.NewGuid():N}.jsonl");
        try
        {
            var lines = new[]
            {
                """{"timestamp":"2026-07-11T00:59:00Z","type":"event_msg","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol"}}}""",
                """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"reasoning_output_tokens":4,"total_tokens":100},"rate_limits":{"primary":{"used_percent":22,"window_minutes":300,"resets_at":1783768376},"secondary":{"used_percent":3,"window_minutes":10080,"resets_at":1784355176},"plan_type":"pro"}}}}""",
                """{"timestamp":"2026-07-11T01:01:00Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{}"}}""",
                """{"timestamp":"2026-07-11T01:02:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":40,"reasoning_output_tokens":8,"total_tokens":160}}}}""",
                """{"timestamp":"2026-07-11T01:03:00Z","type":"event_msg","payload":{"type":"task_started"}}""",
                """{"timestamp":"2026-07-11T01:04:00Z","type":"event_msg","payload":{"type":"task_complete","duration_ms":1500}}""",
            };
            await File.WriteAllLinesAsync(path, lines);

            var result = await CodexSessionReader.ParseFileAsync(path);
            var total = result.UsageBuckets.Aggregate(
                CodexU.Core.TokenBreakdown.Zero,
                (current, bucket) => current.Add(bucket.Tokens));

            Assert.Equal(2, result.TokenEventCount);
            Assert.Equal(160, total.TotalTokens);
            Assert.Equal(120, total.InputTokens);
            Assert.Equal(30, total.CachedInputTokens);
            Assert.Equal(40, total.OutputTokens);
            Assert.All(result.UsageBuckets, bucket => Assert.Equal("gpt-5.6-sol", bucket.Model));
            Assert.Equal(1, result.Tools["exec_command"]);
            Assert.Equal(1, result.TaskLifecycle.Started);
            Assert.Equal(1, result.TaskLifecycle.Completed);
            Assert.Equal(1500, result.TaskLifecycle.DurationMilliseconds);
            Assert.Equal(1500, result.TaskLifecycle.LongestDurationMilliseconds);
            Assert.Equal(22, result.LatestRateLimit?.Primary?.UsedPercent);
            Assert.Equal(300, result.LatestRateLimit?.Primary?.WindowDurationMinutes);
            Assert.Equal("pro", result.LatestRateLimit?.PlanType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_SkipsThreadSpawnForkReplayTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-fork-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"parent","forked_from_id":"parent","thread_source":"subagent","source":{"subagent":{"thread_spawn":{}}}}}""",
                """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"parent","thread_source":"user"}}""",
                """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100}}}}""",
                """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":40,"total_tokens":160}}}}""",
                """{"timestamp":"2026-07-11T01:00:01Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{}"}}""",
                """{"timestamp":"2026-07-11T01:00:01Z","type":"event_msg","payload":{"type":"task_started"}}""",
                """{"timestamp":"2026-07-11T01:00:01Z","type":"event_msg","payload":{"type":"task_complete","duration_ms":800}}""",
                """{"timestamp":"2026-07-11T01:00:02Z","type":"inter_agent_communication_metadata","payload":{"trigger_turn":true}}""",
                """{"timestamp":"2026-07-11T01:00:02Z","type":"response_item","payload":{"type":"function_call","name":"apply_patch","arguments":"{}"}}""",
                """{"timestamp":"2026-07-11T01:00:02Z","type":"event_msg","payload":{"type":"task_started"}}""",
                """{"timestamp":"2026-07-11T01:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":160,"cached_input_tokens":40,"output_tokens":60,"total_tokens":220}}}}"""
            ]);

            var result = await CodexSessionReader.ParseFileAsync(path);
            var total = result.UsageBuckets.Aggregate(
                TokenBreakdown.Zero,
                (current, bucket) => current.Add(bucket.Tokens));

            Assert.Equal(1, result.TokenEventCount);
            Assert.Equal(60, total.TotalTokens);
            Assert.Equal(40, total.InputTokens);
            Assert.Equal(10, total.CachedInputTokens);
            Assert.Equal(20, total.OutputTokens);
            Assert.False(result.Tools.ContainsKey("exec_command"));
            Assert.Equal(1, result.Tools["apply_patch"]);
            Assert.Equal(1, result.TaskLifecycle.Started);
            Assert.Equal(0, result.TaskLifecycle.Completed);
            Assert.Equal(ForkReplayPhase.Completed, result.ForkReplayPhase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_CountsNonThreadSpawnSubagentTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-subagent-other-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"parent","thread_source":"subagent","source":{"subagent":{"other":"guardian"}}}}""",
                """{"timestamp":"2026-07-11T01:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100}}}}"""
            ]);

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.Equal(100, Assert.Single(result.UsageBuckets).Tokens.TotalTokens);
            Assert.Equal(ForkReplayPhase.NotThreadSpawn, result.ForkReplayPhase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_DoesNotSkipAtMarkerWithoutMatchingParentMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-fork-no-parent-meta-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"parent","forked_from_id":"parent","thread_source":"subagent","source":{"subagent":{"thread_spawn":{}}}}}""",
                """{"timestamp":"2026-07-11T01:00:01Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100}}}}""",
                """{"timestamp":"2026-07-11T01:00:02Z","type":"inter_agent_communication_metadata","payload":{"trigger_turn":true}}""",
                """{"timestamp":"2026-07-11T01:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":40,"total_tokens":160}}}}"""
            ]);

            var result = await CodexSessionReader.ParseFileAsync(path);
            var total = result.UsageBuckets.Aggregate(
                TokenBreakdown.Zero,
                (current, bucket) => current.Add(bucket.Tokens));

            Assert.Equal(160, total.TotalTokens);
            Assert.Equal(ForkReplayPhase.AwaitingParentMetadata, result.ForkReplayPhase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_PersistsForkReplayStateAcrossIncrementalAppend()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-fork-replay-index-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-fork-replay.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"parent","forked_from_id":"parent","thread_source":"subagent","source":{"subagent":{"thread_spawn":{}}}}}""",
            """{"timestamp":"2026-07-11T01:00:00Z","type":"session_meta","payload":{"id":"parent","thread_source":"user"}}""",
            """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":120,"cached_input_tokens":30,"output_tokens":40,"total_tokens":160}}}}"""
        ]);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            Assert.Equal(160, first.Tokens.Lifetime.Tokens);
            Assert.Equal(DataQuality.Partial, first.Tokens.Lifetime.Quality);

            await File.AppendAllTextAsync(
                path,
                """{"timestamp":"2026-07-11T01:00:02Z","type":"inter_agent_communication_metadata","payload":{"trigger_turn":true}}"""
                + Environment.NewLine
                + """{"timestamp":"2026-07-11T01:00:03Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":160,"cached_input_tokens":40,"output_tokens":60,"total_tokens":220}}}}"""
                + Environment.NewLine);

            var second = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, second.IndexStatus.IncrementalFiles);
            Assert.Equal(60, second.Tokens.Lifetime.Tokens);
            Assert.Equal(1, second.TokenEventCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileAsync_PreaggregatesSameDayAndModelEvents()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-{Guid.NewGuid():N}.jsonl");
        try
        {
            var lines = new List<string>
            {
                """{"timestamp":"2026-07-11T00:59:00Z","payload":{"type":"thread_settings_applied","thread_settings":{"model":"gpt-5.6-sol"}}}"""
            };
            for (var index = 1; index <= 100; index++)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    timestamp = $"2026-07-11T01:{index % 60:00}:00Z",
                    payload = new
                    {
                        type = "token_count",
                        info = new
                        {
                            total_token_usage = new
                            {
                                input_tokens = index * 10,
                                cached_input_tokens = 0,
                                output_tokens = 0,
                                total_tokens = index * 10
                            }
                        }
                    }
                }));
            }

            await File.WriteAllLinesAsync(path, lines);

            var result = await CodexSessionReader.ParseFileAsync(path);
            var bucket = Assert.Single(result.UsageBuckets);

            Assert.Equal("gpt-5.6-sol", bucket.Model);
            Assert.Equal(100, bucket.EventCount);
            Assert.Equal(100, result.TokenEventCount);
            Assert.Equal(1000, bucket.Tokens.TotalTokens);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_IgnoresPartiallyWrittenJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "{not-complete");

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.Empty(result.UsageBuckets);
            Assert.Empty(result.Tools);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_CountsValidFinalJsonWithoutTrailingNewline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path,
                """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100}}}}""");

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.Equal(1, result.TokenEventCount);
            Assert.Equal(100, Assert.Single(result.UsageBuckets).Tokens.TotalTokens);
            Assert.Equal(new FileInfo(path).Length, result.Offset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ParseFileAsync_IgnoresValidNonObjectJsonLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(path, "[]");

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.Empty(result.UsageBuckets);
            Assert.Equal(new FileInfo(path).Length, result.Offset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_ReusesUnchangedSessionFromPersistentIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-index-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-test.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-07-11T00:59:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}""",
            """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":10,"total_tokens":110}}}}"""
        ]);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            var second = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, first.IndexStatus.ParsedFiles);
            Assert.Equal(0, first.IndexStatus.ReusedFiles);
            Assert.Equal(1, second.IndexStatus.ReusedFiles);
            Assert.Equal(0, second.IndexStatus.ParsedFiles);

            await File.AppendAllTextAsync(path,
                """{"timestamp":"2026-07-11T01:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":140,"cached_input_tokens":30,"output_tokens":20,"total_tokens":160}}}}""" + Environment.NewLine);
            var third = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, third.IndexStatus.IncrementalFiles);
            Assert.Equal(160, third.Tokens.Lifetime.Breakdown.TotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ToleratesDuplicatePathsInPersistentIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-duplicate-index-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-duplicate.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-07-11T00:59:00Z","type":"turn_context","payload":{"model":"gpt-5.4"}}""",
            """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":10,"total_tokens":110}}}}"""
        ]);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            _ = await new CodexSessionReader(paths, true, root).ReadAsync();
            var indexPath = Path.Combine(root, "session-index-v1.json");
            var json = await File.ReadAllTextAsync(indexPath);
            var rootNode = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
            var entries = rootNode["entries"]!.AsArray();
            var duplicate = entries[0]!.DeepClone().AsObject();
            duplicate["parsed"] = null;
            entries.Add(duplicate);
            await File.WriteAllTextAsync(indexPath, rootNode.ToJsonString());

            var result = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, result.IndexStatus.ReusedFiles);
            Assert.Equal(110, result.Tokens.Lifetime.Breakdown.TotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ResumesFromLastCompleteLineAfterPartialAppend()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-partial-index-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-partial.jsonl");
        var firstLine = """{"timestamp":"2026-07-11T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100}}}}""";
        var partialSecondLine = """{"timestamp":"2026-07-11T01:01:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1""";
        var secondLineRemainder = """20,"cached_input_tokens":30,"output_tokens":40,"total_tokens":160}}}}""";
        await File.WriteAllTextAsync(path, firstLine + Environment.NewLine + partialSecondLine);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var first = await new CodexSessionReader(paths, true, root).ReadAsync();
            Assert.Equal(100, first.Tokens.Lifetime.Breakdown.TotalTokens);

            await File.AppendAllTextAsync(path, secondLineRemainder + Environment.NewLine);
            var second = await new CodexSessionReader(paths, true, root).ReadAsync();

            Assert.Equal(1, second.IndexStatus.IncrementalFiles);
            Assert.Equal(2, second.TokenEventCount);
            Assert.Equal(160, second.Tokens.Lifetime.Breakdown.TotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotExposeQuotaPastItsResetTime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-stale-quota-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var path = Path.Combine(sessions, "rollout-stale.jsonl");
        await File.WriteAllLinesAsync(path,
        [
            """{"timestamp":"2026-01-01T01:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80,"cached_input_tokens":20,"output_tokens":20,"total_tokens":100},"rate_limits":{"primary":{"used_percent":80,"window_minutes":300,"resets_at":1},"plan_type":"pro"}}}}"""
        ]);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Null(result.PrimaryQuota);
            Assert.Contains(result.Diagnostics, item => item.Contains("超过重置时间", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_PreservesQuotaWhenNoTokenUsageEventIsAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-quota-only-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    rate_limits = new
                    {
                        primary = new { used_percent = 31, window_minutes = 300, resets_at = reset },
                        plan_type = "pro"
                    }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-quota.jsonl"), line);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(0, result.TokenEventCount);
            Assert.Equal(31, result.PrimaryQuota?.UsedPercent);
            Assert.Equal("pro", result.Account?.PlanType);
            Assert.Equal(DataQuality.Unavailable, result.Tokens.Lifetime.Quality);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ClassifiesWeeklyOnlyPrimaryFieldAsSevenDayQuota()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-weekly-quota-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var reset = DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds();
        var line = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    rate_limits = new
                    {
                        primary = new { used_percent = 31, window_minutes = 10_080, resets_at = reset },
                        plan_type = "prolite"
                    }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-weekly.jsonl"), line);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Null(result.PrimaryQuota);
            Assert.Equal(31, result.SecondaryQuota?.UsedPercent);
            Assert.Equal(10_080, result.SecondaryQuota?.WindowDurationMinutes);
            Assert.Equal("prolite", result.Account?.PlanType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileAsync_MergesQuotaWindowsFromDifferentEventsIndependently()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-quota-merge-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                """{"timestamp":"2026-07-11T01:00:00Z","payload":{"type":"token_count","info":{"rate_limits":{"primary":{"used_percent":61,"window_minutes":300,"resets_at":1783768376},"plan_type":"pro"}}}}""",
                """{"timestamp":"2026-07-11T01:01:00Z","payload":{"type":"token_count","info":{"rate_limits":{"primary":{"used_percent":31,"window_minutes":10080,"resets_at":1784355176},"plan_type":"prolite"}}}}"""
            ]);

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.Equal(61, result.LatestRateLimit?.Primary?.UsedPercent);
            Assert.Equal(300, result.LatestRateLimit?.Primary?.WindowDurationMinutes);
            Assert.Equal(31, result.LatestRateLimit?.Secondary?.UsedPercent);
            Assert.Equal(10_080, result.LatestRateLimit?.Secondary?.WindowDurationMinutes);
            Assert.Equal("prolite", result.LatestRateLimit?.PlanType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadAsync_DatesQuotaByWhenTheRolloutRecordedIt()
    {
        // The burn-rate forecast divides consumption by elapsed time. The rollout
        // already carries the moment each reading was taken; dropping it dated Codex
        // readings by the refresh that picked them up, so a window read after the app
        // had been closed for hours looked like it had barely moved and the warning
        // that should have fired did not.
        var root = Path.Combine(Path.GetTempPath(), $"codexu-quota-measured-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.UtcNow;
        var recordedAt = now.AddMinutes(-37);
        var line = JsonSerializer.Serialize(new
        {
            timestamp = recordedAt.ToString("O"),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    rate_limits = new
                    {
                        primary = new
                        {
                            used_percent = 44,
                            window_minutes = 300,
                            resets_at = now.AddHours(2).ToUnixTimeSeconds()
                        },
                        secondary = new
                        {
                            used_percent = 12,
                            window_minutes = 10_080,
                            resets_at = now.AddDays(5).ToUnixTimeSeconds()
                        },
                        plan_type = "pro"
                    }
                }
            }
        });
        // The reader globs rollout-*.jsonl; a file named rollout.jsonl is invisible.
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-measured.jsonl"), line);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.NotNull(result.PrimaryQuota);
            Assert.NotNull(result.PrimaryQuota.MeasuredAt);
            Assert.Equal(recordedAt, result.PrimaryQuota.MeasuredAt.Value, TimeSpan.FromSeconds(2));
            Assert.NotNull(result.SecondaryQuota);
            Assert.NotNull(result.SecondaryQuota.MeasuredAt);
            Assert.Equal(recordedAt, result.SecondaryQuota.MeasuredAt.Value, TimeSpan.FromSeconds(2));
            // The stamp must not be mistaken for a dropped sample.
            Assert.DoesNotContain(result.Diagnostics, line => line.Contains("已忽略"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MergesQuotaWindowsAcrossSessionFilesIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-cross-file-quota-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.UtcNow;
        var fiveHourLine = JsonSerializer.Serialize(new
        {
            timestamp = now.AddMinutes(-1).ToString("O"),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    rate_limits = new
                    {
                        primary = new
                        {
                            used_percent = 61,
                            window_minutes = 300,
                            resets_at = now.AddHours(2).ToUnixTimeSeconds()
                        },
                        plan_type = "pro"
                    }
                }
            }
        });
        var weeklyLine = JsonSerializer.Serialize(new
        {
            timestamp = now.ToString("O"),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    rate_limits = new
                    {
                        primary = new
                        {
                            used_percent = 31,
                            window_minutes = 10_080,
                            resets_at = now.AddDays(6).ToUnixTimeSeconds()
                        },
                        plan_type = "prolite"
                    }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-five-hour.jsonl"), fiveHourLine);
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-weekly.jsonl"), weeklyLine);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(61, result.PrimaryQuota?.UsedPercent);
            Assert.Equal(31, result.SecondaryQuota?.UsedPercent);
            Assert.Equal("prolite", result.Account?.PlanType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotCountFutureDatedUsageInCurrentPeriods()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-future-session-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-future.jsonl"),
            CreateTokenLine(DateTimeOffset.Now.AddDays(2).ToString("O")));
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(100, result.Tokens.Lifetime.Tokens);
            Assert.Equal(0, result.Tokens.Today.Tokens);
            Assert.Equal(0, result.Tokens.SevenDays.Tokens);
            Assert.Equal(0, result.Tokens.Month.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_MarksTokenDataPartialWhenAnySessionCannotBeRead()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-partial-read-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-good.jsonl"),
            CreateTokenLine(timestamp));
        var lockedPath = Path.Combine(sessions, "rollout-locked.jsonl");
        await File.WriteAllTextAsync(lockedPath, "{}");
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");

        try
        {
            await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await new CodexSessionReader(paths, false, root).ReadAsync();

            Assert.Equal(1, result.SkippedFileCount);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Equal(100, result.Tokens.Lifetime.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalReader_DoesNotMixSqliteLifetimeIntoPartialJsonlLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-partial-merge-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var database = Path.Combine(root, "state_5.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, tokens_used INTEGER, updated_at INTEGER);
                INSERT INTO threads (id, title, tokens_used, updated_at) VALUES ('thread-1', 'task', 500, $updated);
                """;
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-good.jsonl"),
            CreateTokenLine(timestamp));
        var lockedPath = Path.Combine(sessions, "rollout-locked.jsonl");
        await File.WriteAllTextAsync(lockedPath, "{}");
        var paths = new CodexPaths(root, root, database, sessions, "missing", "missing", "missing", "missing");

        try
        {
            await using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var result = await new CodexLocalUsageReader(paths, incrementalIndexEnabled: false).ReadAsync();

            Assert.Equal(100, result.Tokens.Lifetime.Tokens);
            Assert.Equal(100, result.Tokens.Lifetime.Breakdown.TotalTokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Equal(100, result.Tokens.Lifetime.UnratedTokens);
            Assert.Equal(100, result.Tokens.Today.Tokens);
            Assert.Equal(100, result.DailyUsage.Sum(item => item.Tokens));
            Assert.Contains(result.Diagnostics, item => item.Contains("SQLite 线程累计值未合并", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalReader_KeepsJsonlLedgerWhenSessionLineIsInvalid(bool oversizedLine)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-partial-line-merge-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var database = Path.Combine(root, "state_5.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, tokens_used INTEGER, updated_at INTEGER, model TEXT);
                INSERT INTO threads (id, title, tokens_used, updated_at, model)
                VALUES ('thread-1', 'task', 500, $updated, 'gpt-5.4');
                """;
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var modelLine = JsonSerializer.Serialize(new
        {
            timestamp,
            payload = new
            {
                type = "thread_settings_applied",
                thread_settings = new { model = "gpt-5.6-sol" }
            }
        });
        var invalidLine = oversizedLine
            ? new string('x', BoundedLineReader.DefaultMaximumLineBytes + 1)
            : "{ malformed-but-complete";
        await File.WriteAllTextAsync(
            Path.Combine(sessions, "rollout-invalid-line.jsonl"),
            modelLine
            + Environment.NewLine
            + CreateTokenLine(timestamp)
            + Environment.NewLine
            + invalidLine
            + Environment.NewLine);
        var paths = new CodexPaths(root, root, database, sessions, "missing", "missing", "missing", "missing");

        try
        {
            var result = await new CodexLocalUsageReader(paths, incrementalIndexEnabled: false).ReadAsync();

            Assert.Equal(100, result.Tokens.Lifetime.Tokens);
            Assert.Equal(100, result.Tokens.Lifetime.Breakdown.TotalTokens);
            Assert.Equal(DataQuality.Partial, result.Tokens.Lifetime.Quality);
            Assert.Equal(0, result.Tokens.Lifetime.UnratedTokens);
            Assert.Equal(100, result.Tokens.Today.Tokens);
            Assert.Equal(100, result.DailyUsage.Sum(item => item.Tokens));
            Assert.Contains(result.Diagnostics, item => item.Contains("SQLite 线程累计值未合并", StringComparison.Ordinal));
            var model = Assert.Single(result.Models);
            Assert.Equal("gpt-5.6-sol", model.Model);
            Assert.Equal(100, model.Tokens);
            Assert.DoesNotContain(result.Models, item => item.Model == "gpt-5.4");
            var rated = Assert.Single(result.Tokens.Lifetime.CreditsByModel);
            Assert.Equal("gpt-5.6-sol", rated.Model);
            Assert.Equal(100, rated.Tokens.VisibleTotalTokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotExposeQuotaWithoutResetAfterSampleWindowExpires()
    {
        var result = await ReadSingleQuotaAsync(
            DateTimeOffset.UtcNow.AddHours(-6),
            resetsAt: null,
            windowMinutes: 300);

        Assert.Null(result.PrimaryQuota);
        Assert.Contains(result.Diagnostics, item => item.Contains("超过重置时间", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_UsesExplicitFutureResetEvenWhenSampleTimestampIsOld()
    {
        var result = await ReadSingleQuotaAsync(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddHours(1),
            windowMinutes: 300);

        Assert.Equal(47, result.PrimaryQuota?.UsedPercent);
    }

    [Fact]
    public async Task LocalReader_PinnedCatalogDoesNotFallBackToBuiltInRates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-pinned-session-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var timestamp = DateTimeOffset.Now.ToString("O");
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "rollout-pinned.jsonl"),
            [
                JsonSerializer.Serialize(new
                {
                    timestamp,
                    type = "event_msg",
                    payload = new
                    {
                        type = "thread_settings_applied",
                        thread_settings = new { model = "gpt-5.6-sol" }
                    }
                }),
                CreateTokenLine(timestamp)
            ]);
        var paths = new CodexPaths(
            root,
            root,
            Path.Combine(root, "missing.sqlite"),
            sessions,
            "missing",
            "missing",
            "missing",
            "missing");
        var pinnedRates = new[]
        {
            new ModelCreditRate("archive-model", 10, 1, 100)
        };

        try
        {
            var result = await new CodexLocalUsageReader(
                paths,
                incrementalIndexEnabled: false,
                customRates: pinnedRates,
                completeRateCatalog: true).ReadAsync();

            Assert.Equal(0, result.Tokens.Lifetime.CreditsUsed);
            Assert.Equal(100, result.Tokens.Lifetime.UnratedTokens);
            Assert.Empty(result.Tokens.Lifetime.CreditsByModel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ParseFileAsync_PreservesPlanOnlyRateLimitEvent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codexu-plan-only-{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllTextAsync(
                path,
                """{"timestamp":"2026-07-11T01:00:00Z","payload":{"type":"token_count","info":{"rate_limits":{"plan_type":"plus"}}}}""");

            var result = await CodexSessionReader.ParseFileAsync(path);

            Assert.NotNull(result.LatestRateLimit);
            Assert.Null(result.LatestRateLimit.Primary);
            Assert.Null(result.LatestRateLimit.Secondary);
            Assert.Equal("plus", result.LatestRateLimit.PlanType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateTokenLine(string timestamp) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            info = new
            {
                total_token_usage = new
                {
                    input_tokens = 80,
                    cached_input_tokens = 20,
                    output_tokens = 20,
                    total_tokens = 100
                }
            }
        }
    });

    private static async Task<SessionAnalytics> ReadSingleQuotaAsync(
        DateTimeOffset timestamp,
        DateTimeOffset? resetsAt,
        int windowMinutes)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-quota-freshness-{Guid.NewGuid():N}");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(sessions);
        var quota = new Dictionary<string, object?>
        {
            ["used_percent"] = 47,
            ["window_minutes"] = windowMinutes
        };
        if (resetsAt is { } reset)
        {
            quota["resets_at"] = reset.ToUnixTimeSeconds();
        }

        var line = JsonSerializer.Serialize(new
        {
            timestamp = timestamp.ToString("O"),
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new { input_tokens = 80, output_tokens = 20, total_tokens = 100 },
                    rate_limits = new { primary = quota }
                }
            }
        });
        await File.WriteAllTextAsync(Path.Combine(sessions, "rollout-quota.jsonl"), line);
        var paths = new CodexPaths(root, root, "missing.db", sessions, "missing", "missing", "missing", "missing");
        try
        {
            return await new CodexSessionReader(paths, false, root).ReadAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
