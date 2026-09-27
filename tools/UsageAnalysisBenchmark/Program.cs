using System.Diagnostics;
using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

// Synthetic data only. Measure query/paging work separately from filesystem indexing.
var count = args.Length > 0 ? int.Parse(args[0]) : 10_000;
var date = new DateOnly(2026, 9, 1);
var entries = Enumerable.Range(0, count).Select(i => new UsageAnalysisEntry(
    date.AddDays(i % 30), $"session-{i / 10}", null, null, "D:/Synthetic",
    "test-model", "tasks", 100, new(80, 20, 20, 0, 100), null, null, "retained",
    AvailableBreakdownFields: ["input", "cachedInput", "output"])).ToArray();
var empty = DashboardSnapshot.Empty(AgentRuntime.Codex);
var reader = new SyntheticReader(new(null, null, null, empty.Tokens, [], [], [], [], [], [], [], [],
    empty.TaskLifecycle, empty.IndexStatus, [], AnalysisData: new(entries, [])));
var service = new DashboardService(new EmptyAccount(), reader);
await service.QueryUsageAsync(new()); // JIT and cold selection are outside the paging sample.
var allocated = GC.GetTotalAllocatedBytes(true);
var timer = Stopwatch.StartNew();
long checksum = 0;
for (var page = 1; page <= 10; page++)
    checksum += (await service.QueryUsageAsync(new(Page: page))).Totals.Tokens;
timer.Stop();
Console.WriteLine(JsonSerializer.Serialize(new
{
    entries = count, pages = 10, elapsedMs = timer.Elapsed.TotalMilliseconds,
    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
    sourceReads = reader.Reads, checksum
}));

sealed class SyntheticReader(LocalUsageSnapshot snapshot) : ILocalUsageReader
{
    public int Reads { get; private set; }
    public Task<LocalUsageSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(snapshot);
    }
}
sealed class EmptyAccount : IAppServerClient
{
    public Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppServerSnapshot(null, null, null, []));
}
