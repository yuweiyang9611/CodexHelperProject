using CodexU.Core;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure;

public sealed class GoalsReader(CodexPaths paths)
{
    public async Task<(IReadOnlyList<GoalItem> Items, string Diagnostic)> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = new[]
        {
            paths.GoalsDatabase,
            Path.Combine(paths.CodexDirectory, "sqlite", "goals_1.sqlite")
        };
        var database = candidates.FirstOrDefault(File.Exists);
        if (database is null)
        {
            return ([], "Goals：未找到 goals_1.sqlite");
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 2,
                Pooling = false
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            var columns = await ReadColumnsAsync(connection, cancellationToken);
            if (!columns.Contains("goal_id") || !columns.Contains("objective"))
            {
                return ([], "Goals：数据库结构暂不兼容");
            }

            var status = columns.Contains("status") ? "COALESCE(status, '')" : "''";
            var tokenBudget = columns.Contains("token_budget") ? "token_budget" : "NULL";
            var tokensUsed = columns.Contains("tokens_used") ? "COALESCE(tokens_used, 0)" : "0";
            var timeUsed = columns.Contains("time_used_seconds") ? "COALESCE(time_used_seconds, 0)" : "0";
            var updated = columns.Contains("updated_at_ms") ? "updated_at_ms" : "NULL";
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT goal_id, objective, {status}, {tokenBudget}, {tokensUsed}, {timeUsed}, {updated}
                FROM thread_goals
                ORDER BY {updated} DESC
                LIMIT 24;
                """;
            var result = new List<GoalItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new GoalItem(
                    Convert.ToString(reader.GetValue(0)) ?? Guid.NewGuid().ToString("N"),
                    reader.IsDBNull(1) ? "未命名目标" : reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3)),
                    reader.IsDBNull(4) ? 0 : Convert.ToInt64(reader.GetValue(4)),
                    reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5)),
                    reader.IsDBNull(6) ? null : UsageCredits.FromUnixTime(Convert.ToInt64(reader.GetValue(6)))));
            }

            return (result, $"Goals：已读取 {result.Count} 个目标 · {database}");
        }
        catch (SqliteException ex)
        {
            return ([], $"Goals：读取失败 {ex.SqliteErrorCode}");
        }
        catch (IOException ex)
        {
            return ([], $"Goals：读取失败 {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "Goals：无权读取目标数据库");
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return ([], $"Goals：字段值不兼容 {ex.Message}");
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(thread_goals);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }

        return result;
    }
}
