using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class QuotaSampleStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Append_IgnoresARepeatOfTheNewestReading()
    {
        // A refresh that reports the same figure is the same measurement seen twice.
        // Recording it would stretch the measured span without adding information,
        // flattening the burn rate and under-reporting how fast quota is going.
        var existing = new[] { Sample(0, 40) };

        var appended = QuotaSampleStore.Append(existing, Sample(5, 40));

        Assert.Single(appended);
        Assert.Equal(Now, appended[0].ObservedAt);
    }

    [Fact]
    public void Append_KeepsAReadingThatActuallyMoved()
    {
        var appended = QuotaSampleStore.Append([Sample(0, 40)], Sample(5, 41));

        Assert.Equal(2, appended.Count);
        Assert.Equal(41, appended[^1].UsedPercent);
    }

    [Fact]
    public void Append_RejectsAReadingThatIsNotNewerThanTheLast()
    {
        var existing = new[] { Sample(10, 40) };

        Assert.Same(existing, QuotaSampleStore.Append(existing, Sample(5, 55)));
        Assert.Same(existing, QuotaSampleStore.Append(existing, Sample(10, 55)));
    }

    [Fact]
    public void Append_KeepsARepeatedPercentWhenTheWindowChanged()
    {
        // Same percentage but a new reset time is a different window instance, not
        // a duplicate reading.
        var existing = new[] { new QuotaSample(Now, 40, Now.AddHours(1)) };

        var appended = QuotaSampleStore.Append(existing, new QuotaSample(Now.AddMinutes(5), 40, Now.AddHours(6)));

        Assert.Equal(2, appended.Count);
    }

    [Fact]
    public async Task SaveAsync_RoundTripsAndDropsReadingsPastRetention()
    {
        var root = CreateRoot();
        try
        {
            var store = new QuotaSampleStore(root);
            var key = QuotaSampleStore.WindowKey(AgentRuntime.ClaudeCode, "five-hour", "statusline");
            var windows = new Dictionary<string, IReadOnlyList<QuotaSample>>(StringComparer.Ordinal)
            {
                [key] = [Sample(-600, 5), Sample(-20, 30), Sample(0, 40)],
            };

            await store.SaveAsync(windows, Now, TimeSpan.FromMinutes(90));
            var loaded = await store.LoadAsync();

            var samples = Assert.Contains(key, loaded);
            Assert.Equal(2, samples.Count);
            Assert.Equal([30d, 40d], samples.Select(sample => sample.UsedPercent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowKey_SeparatesRuntimesWindowsAndSources()
    {
        // Codex can report a window from the app server or from local rollouts.
        // Measuring a rate across a switch between them would compare two different
        // measurements, so the source is part of the identity.
        var claude = QuotaSampleStore.WindowKey(AgentRuntime.ClaudeCode, "five-hour", "statusline");
        var codex = QuotaSampleStore.WindowKey(AgentRuntime.Codex, "five-hour", "statusline");
        var sevenDay = QuotaSampleStore.WindowKey(AgentRuntime.ClaudeCode, "seven-day", "statusline");
        var otherSource = QuotaSampleStore.WindowKey(AgentRuntime.Codex, "five-hour", "app-server");

        Assert.Equal(4, new[] { claude, codex, sevenDay, otherSource }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task UpdateAsync_MakesTheSecondUpdateSeeTheFirstOnesWrite()
    {
        // Both runtimes share one file. If each read the trails and then wrote them
        // back independently, the later write would silently drop the reading the
        // earlier one had just added.
        var root = CreateRoot();
        try
        {
            var store = new QuotaSampleStore(root);
            var retention = TimeSpan.FromHours(1);
            var firstIsInside = new TaskCompletionSource();
            var releaseFirst = new TaskCompletionSource();
            IReadOnlyList<QuotaSample>? seenBySecond = null;

            // On its own thread: an uncontended gate is acquired synchronously, so the
            // callback would otherwise run on — and block — this one.
            var first = Task.Run(() => store.UpdateAsync(
                trails =>
                {
                    firstIsInside.SetResult();
                    releaseFirst.Task.GetAwaiter().GetResult();
                    trails["a"] = [Sample(0, 40)];
                    return 0;
                },
                Now,
                retention));

            await firstIsInside.Task;
            var second = Task.Run(() => store.UpdateAsync(
                trails =>
                {
                    seenBySecond = trails.TryGetValue("a", out var trail) ? trail : null;
                    return 0;
                },
                Now,
                retention));

            releaseFirst.SetResult();
            await Task.WhenAll(first, second);

            Assert.NotNull(seenBySecond);
            Assert.Single(seenBySecond);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsNothingBeforeAnythingIsRecorded()
    {
        var root = CreateRoot();
        try
        {
            Assert.Empty(await new QuotaSampleStore(root).LoadAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static QuotaSample Sample(int minutesFromNow, double usedPercent) =>
        new(Now.AddMinutes(minutesFromNow), usedPercent, null);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-quota-samples-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
