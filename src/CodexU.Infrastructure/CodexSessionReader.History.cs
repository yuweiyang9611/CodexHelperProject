using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

public sealed partial class CodexSessionReader
{
    private sealed record SessionMetadata(string? Workspace, string? Title);
    private sealed class WorkspaceEnrichment
    {
        internal SessionMetadata? Metadata;
        internal ParsedSessionFile? Parsed;
    }
    private sealed class WorkspaceEnrichments
    {
        internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<ParsedSessionFile, WorkspaceEnrichment> Values = new();
    }
    private PhysicalSessionFile EnrichWorkspace(PhysicalSessionFile file, IReadOnlyDictionary<string, SessionMetadata> map)
    {
        if (!map.TryGetValue(file.Parsed.SessionId ?? "", out var metadata)) return file;
        var enriched = new SessionMetadata(file.Parsed.Workspace ?? metadata.Workspace, metadata.Title ?? file.Parsed.Title);
        if (file.Parsed.Workspace == enriched.Workspace && file.Parsed.Title == enriched.Title) return file;
        var entry = UsageReadContext.For(indexDirectory).Get("codex-workspaces", () => new WorkspaceEnrichments())
            .Values.GetValue(file.Parsed, _ => new WorkspaceEnrichment());
        if (entry.Parsed is null || entry.Metadata != enriched)
        {
            entry.Metadata = enriched;
            entry.Parsed = file.Parsed with { Workspace = enriched.Workspace, Title = enriched.Title };
        }
        return file with { Parsed = entry.Parsed };
    }

    private static bool CanReplaceHistory(ParsedSessionFile previous, ParsedSessionFile next) =>
        next.Offset >= previous.Offset
        && next.TokenEvents.Count >= previous.TokenEvents.Count
        && previous.TokenEvents.Select(e => e with { AvailableFields = null })
            .SequenceEqual(next.TokenEvents.Take(previous.TokenEvents.Count).Select(e => e with { AvailableFields = null }))
        && previous.Tools.All(pair => next.Tools.GetValueOrDefault(pair.Key) >= pair.Value)
        && previous.Skills.All(pair => next.Skills.GetValueOrDefault(pair.Key) >= pair.Value)
        && (previous.Workspace is null || string.Equals(previous.Workspace, next.Workspace, StringComparison.OrdinalIgnoreCase));

    private static async Task<Dictionary<string, SessionMetadata>> ReadWorkspaceMapAsync(string database, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SessionMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(database)) return result;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            await db.OpenAsync(cancellationToken);
            using var command = db.CreateCommand();
            command.CommandText = "PRAGMA table_info(threads)";
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var schema = await command.ExecuteReaderAsync(cancellationToken))
                while (await schema.ReadAsync(cancellationToken)) columns.Add(schema.GetString(1));
            if (!columns.Contains("id")) return result;
            command.CommandText = $"SELECT id,{(columns.Contains("cwd") ? "cwd" : "NULL")},{(columns.Contains("title") ? "title" : "NULL")} FROM threads";
            using var rows = await command.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
                if (!rows.IsDBNull(0))
                    result[rows.GetString(0)] = new(rows.IsDBNull(1) ? null : WorkspaceScope.Normalize(rows.GetString(1)),
                        rows.IsDBNull(2) || string.IsNullOrWhiteSpace(rows.GetString(2)) ? null : rows.GetString(2));
        }
        catch (SqliteException) { /* Optional metadata: unknown ownership stays explicit. */ }
        return result;
    }
}
