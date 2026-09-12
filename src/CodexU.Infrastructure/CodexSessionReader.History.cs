using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

public sealed partial class CodexSessionReader
{
    private sealed class WorkspaceEnrichment
    {
        internal string? Workspace;
        internal ParsedSessionFile? Parsed;
    }
    private sealed class WorkspaceEnrichments
    {
        internal readonly System.Runtime.CompilerServices.ConditionalWeakTable<ParsedSessionFile, WorkspaceEnrichment> Values = new();
    }
    private PhysicalSessionFile EnrichWorkspace(PhysicalSessionFile file, IReadOnlyDictionary<string, string> map)
    {
        if (file.Parsed.Workspace is not null || !map.TryGetValue(file.Parsed.SessionId ?? "", out var workspace)) return file;
        var entry = UsageReadContext.For(indexDirectory).Get("codex-workspaces", () => new WorkspaceEnrichments())
            .Values.GetValue(file.Parsed, _ => new WorkspaceEnrichment());
        if (entry.Parsed is null || entry.Workspace != workspace)
        {
            entry.Workspace = workspace;
            entry.Parsed = file.Parsed with { Workspace = workspace };
        }
        return file with { Parsed = entry.Parsed };
    }

    private static bool CanReplaceHistory(ParsedSessionFile previous, ParsedSessionFile next) =>
        next.Offset >= previous.Offset
        && next.TokenEvents.Count >= previous.TokenEvents.Count
        && previous.TokenEvents.SequenceEqual(next.TokenEvents.Take(previous.TokenEvents.Count))
        && previous.Tools.All(pair => next.Tools.GetValueOrDefault(pair.Key) >= pair.Value)
        && previous.Skills.All(pair => next.Skills.GetValueOrDefault(pair.Key) >= pair.Value)
        && (previous.Workspace is null || string.Equals(previous.Workspace, next.Workspace, StringComparison.OrdinalIgnoreCase));

    private static async Task<Dictionary<string, string>> ReadWorkspaceMapAsync(string database, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(database)) return result;
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 2 }.ToString());
            await db.OpenAsync(cancellationToken);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT id,cwd FROM threads";
            using var rows = await command.ExecuteReaderAsync(cancellationToken);
            while (await rows.ReadAsync(cancellationToken))
                if (!rows.IsDBNull(0) && !rows.IsDBNull(1) && WorkspaceScope.Normalize(rows.GetString(1)) is { } cwd)
                    result[rows.GetString(0)] = cwd;
        }
        catch (SqliteException) { /* Optional metadata: unknown ownership stays explicit. */ }
        return result;
    }
}
