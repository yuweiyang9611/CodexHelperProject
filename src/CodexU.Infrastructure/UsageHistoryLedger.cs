using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

/// <summary>Authoritative source revisions. Missing or incompatible revisions never delete observations.</summary>
internal sealed class UsageHistoryLedger(string directory)
{
    internal const string RelativePath = "history/usage-ledger-v2.sqlite";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record State<T>(Dictionary<string, T> Values, Dictionary<string, string> Payloads,
        HashSet<string> Conflicts, Dictionary<string, T> Observed, HashSet<string> Disputed);
    internal static SemaphoreSlim GateFor(string root) => Gates.GetOrAdd(UsageReadContext.Root(root), _ => new(1, 1));
    internal static string SourceId(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));

    public async Task<LedgerRead<T>> MergeAsync<T>(string runtime, IReadOnlyDictionary<string, T> observations,
        Func<T, T, bool> canReplace, CancellationToken cancellationToken, IReadOnlySet<string>? disputed = null)
    {
        var gate = GateFor(directory);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var path = Path.Combine(directory, RelativePath);
            var context = UsageReadContext.For(directory);
            var memory = context.Get("ledger", () => new UsageReadContext.LedgerMemory());
            var stamp = UsageReadContext.Stamp(path);
            if (memory.Stamp != stamp) memory.Runtimes.Clear();
            memory.Runtimes.TryGetValue(runtime, out var saved);
            var cached = saved as State<T>;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await db.OpenAsync(cancellationToken);
            var initialize = false;
            using (var version = db.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                var number = Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken));
                if (number is not (0 or 2)) throw new InvalidDataException($"Unsupported usage ledger version: {number}");
                initialize = number == 0;
            }
            if (initialize)
            {
                using var schema = db.CreateCommand();
                schema.CommandText = "PRAGMA journal_mode=DELETE; CREATE TABLE IF NOT EXISTS sources(runtime TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,conflict INTEGER NOT NULL DEFAULT 0,PRIMARY KEY(runtime,id)); PRAGMA user_version=2;";
                await schema.ExecuteNonQueryAsync(cancellationToken);
            }
            using var transaction = db.BeginTransaction();
            var values = cached is null ? new Dictionary<string, T>(StringComparer.Ordinal) : new(cached.Values, StringComparer.Ordinal);
            var payloads = cached is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new(cached.Payloads, StringComparer.Ordinal);
            var conflicts = cached is null ? new HashSet<string>(StringComparer.Ordinal) : new(cached.Conflicts, StringComparer.Ordinal);
            if (cached is null)
            {
                using var read = db.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT id,payload,conflict FROM sources WHERE runtime=$runtime";
                read.Parameters.AddWithValue("$runtime", runtime);
                using var rows = await read.ExecuteReaderAsync(cancellationToken);
                while (await rows.ReadAsync(cancellationToken))
                {
                    values.Add(rows.GetString(0), JsonSerializer.Deserialize<T>(rows.GetString(1), Json)!);
                    context.LedgerDeserialized++;
                    payloads.Add(rows.GetString(0), rows.GetString(1));
                    if (rows.GetInt32(2) != 0) conflicts.Add(rows.GetString(0));
                }
            }
            var retained = values.Keys.Where(key => !observations.ContainsKey(key)).ToHashSet(StringComparer.Ordinal);
            var writes = 0;
            foreach (var (id, value) in observations)
            {
                if (cached is not null && cached.Observed.TryGetValue(id, out var observed)
                    && ReferenceEquals(observed, value) && cached.Disputed.Contains(id) == (disputed?.Contains(id) == true)) continue;
                var existing = values.TryGetValue(id, out var previous);
                var rejected = disputed?.Contains(id) == true || (existing && !canReplace(previous!, value));
                var wasConflict = conflicts.Contains(id);
                if (rejected) { conflicts.Add(id); if (!existing) values[id] = value; }
                else { values[id] = value; conflicts.Remove(id); }
                var payload = JsonSerializer.Serialize(values[id], Json);
                if (wasConflict == rejected && payloads.GetValueOrDefault(id) == payload) continue;
                payloads[id] = payload;
                using var write = db.CreateCommand();
                write.Transaction = transaction;
                write.CommandText = "INSERT INTO sources(runtime,id,payload,conflict) VALUES($runtime,$id,$payload,$conflict) ON CONFLICT(runtime,id) DO UPDATE SET payload=excluded.payload,conflict=excluded.conflict";
                write.Parameters.AddWithValue("$runtime", runtime);
                write.Parameters.AddWithValue("$id", id);
                write.Parameters.AddWithValue("$payload", payload);
                write.Parameters.AddWithValue("$conflict", rejected ? 1 : 0);
                await write.ExecuteNonQueryAsync(cancellationToken);
                writes++;
            }
            transaction.Commit();
            context.LedgerWrites += writes;
            memory.Runtimes[runtime] = new State<T>(values, payloads, conflicts,
                new(observations, StringComparer.Ordinal), disputed?.ToHashSet(StringComparer.Ordinal) ?? new(StringComparer.Ordinal));
            memory.Stamp = UsageReadContext.Stamp(path);
            return new(values, retained, conflicts);
        }
        finally { gate.Release(); }
    }
}

internal sealed record LedgerRead<T>(IReadOnlyDictionary<string, T> Sources, IReadOnlySet<string> RetainedIds, IReadOnlySet<string> ConflictIds)
{
    public int RetainedSources => RetainedIds.Count;
    public int Conflicts => ConflictIds.Count;
    public string Kind(string id) => ConflictIds.Contains(id) ? "conflict" : RetainedIds.Contains(id) ? "retained" : "live";
}
