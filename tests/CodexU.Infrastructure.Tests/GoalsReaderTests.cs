using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class GoalsReaderTests
{
    [Fact]
    public async Task ReadAsync_ReadsCompatibleGoalsDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-goals-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "goals_1.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE thread_goals (
                    goal_id TEXT PRIMARY KEY,
                    objective TEXT NOT NULL,
                    status TEXT,
                    token_budget INTEGER,
                    tokens_used INTEGER,
                    time_used_seconds INTEGER,
                    updated_at_ms INTEGER
                );
                INSERT INTO thread_goals VALUES ('g1', '完成 Windows 看板', 'active', 1000, 250, 60, 1700000000000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var paths = new CodexPaths(root, root, "missing.db", "missing", "missing", "missing", database, "missing");
        try
        {
            var result = await new GoalsReader(paths).ReadAsync();

            var goal = Assert.Single(result.Items);
            Assert.Equal("完成 Windows 看板", goal.Objective);
            Assert.Equal(250, goal.TokensUsed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
