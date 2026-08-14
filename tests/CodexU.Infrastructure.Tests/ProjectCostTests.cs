using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ProjectCostTests
{
    [Fact]
    public async Task ClaudeProjects_ReportMeasuredCostAndTheirBranch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-projcost-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var now = DateTimeOffset.Now;
            await File.WriteAllLinesAsync(Path.Combine(projectDirectory, "session.jsonl"),
            [
                Line(root, now.AddHours(-2), 1_000, 500, "feature/old"),
                Line(root, now, 1_000, 500, "feature/current"),
            ]);

            var rates = new[] { new ModelCreditRate("claude-opus-5", 100, 10, 200) };
            var result = await new ClaudeCodeUsageReader(
                CreatePaths(root, claude),
                customRates: rates,
                applicationDataDirectory: Path.Combine(root, "appdata")).ReadAsync();

            var project = Assert.Single(result.Projects);
            Assert.True(project.HasKnownCost);
            // Claude attributes usage per message, so this is measured, not apportioned.
            Assert.False(project.CostIsEstimated);
            // The newest message decides the branch, not whichever line was read last.
            Assert.Equal("feature/current", project.Branch);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClaudeProjects_ReportUnknownRatherThanFreeWhenNothingIsPriced()
    {
        // A project whose models carry no rate prices to zero. Rendering that as
        // US$0.00 beside real tokens reads as "this cost nothing" — the opposite of
        // "we cannot tell".
        var root = Path.Combine(Path.GetTempPath(), $"codexu-projcost-unrated-{Guid.NewGuid():N}");
        var claude = Path.Combine(root, ".claude");
        var projectDirectory = Path.Combine(claude, "projects", "sample");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(projectDirectory, "session.jsonl"),
                [Line(root, DateTimeOffset.Now, 5_000, 2_000, "main")]);

            // completeRateCatalog with no rates means nothing resolves.
            var result = await new ClaudeCodeUsageReader(
                CreatePaths(root, claude),
                customRates: [],
                completeRateCatalog: true,
                applicationDataDirectory: Path.Combine(root, "appdata")).ReadAsync();

            var project = Assert.Single(result.Projects);
            Assert.True(project.Tokens > 0);
            Assert.Null(project.CreditsUsed);
            Assert.False(project.HasKnownCost);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectUsage_TreatsAbsentAndZeroCostAsUnknown()
    {
        Assert.False(Project(null).HasKnownCost);
        Assert.False(Project(0).HasKnownCost);
        Assert.True(Project(0.01).HasKnownCost);
    }

    private static ProjectUsage Project(double? credits) => new(
        "id",
        "name",
        null,
        1_000,
        1,
        null,
        null,
        credits,
        DataQuality.Detailed);

    private static string Line(string cwd, DateTimeOffset timestamp, long input, long output, string branch) =>
        JsonSerializer.Serialize(new
        {
            type = "assistant",
            cwd,
            gitBranch = branch,
            timestamp = timestamp.ToString("O"),
            message = new
            {
                model = "claude-opus-5",
                usage = new { input_tokens = input, output_tokens = output }
            }
        });

    private static CodexPaths CreatePaths(string root, string claude) => new(
        root,
        Path.Combine(root, ".codex"),
        Path.Combine(root, ".codex", "state.sqlite"),
        Path.Combine(root, ".codex", "sessions"),
        Path.Combine(root, ".codex", "archived_sessions"),
        Path.Combine(root, ".codex", "automations"),
        Path.Combine(root, ".codex", "goals.sqlite"),
        claude);
}
