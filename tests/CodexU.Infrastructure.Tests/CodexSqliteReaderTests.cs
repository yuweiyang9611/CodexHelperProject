using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexSqliteReaderTests
{
    [Fact]
    public async Task ReadAsync_SupportsSchemaWithTitleButNoPreviewColumn()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-sqlite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state_5.sqlite");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE threads (
                        id TEXT PRIMARY KEY,
                        title TEXT,
                        tokens_used INTEGER,
                        updated_at INTEGER
                    );
                    INSERT INTO threads (id, title, tokens_used, updated_at)
                    VALUES ('thread-1', '仅有标题的任务', 123, $updated);
                    """;
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await command.ExecuteNonQueryAsync();
            }

            var paths = new CodexPaths(root, root, database, "missing", "missing", "missing", "missing", "missing");
            var result = await new CodexSqliteReader(paths).ReadAsync();

            Assert.Equal(123, result.Tokens.Lifetime.Tokens);
            Assert.Equal("仅有标题的任务", Assert.Single(result.Tasks).Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotUsePromptPreviewAsTaskTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-sqlite-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state_5.sqlite");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE threads (
                        id TEXT PRIMARY KEY,
                        preview TEXT,
                        tokens_used INTEGER,
                        updated_at INTEGER
                    );
                    INSERT INTO threads (id, preview, tokens_used, updated_at)
                    VALUES ('thread-private', 'private prompt body', 42, $updated);
                    """;
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await command.ExecuteNonQueryAsync();
            }

            var paths = new CodexPaths(root, root, database, "missing", "missing", "missing", "missing", "missing");
            var result = await new CodexSqliteReader(paths).ReadAsync();

            Assert.Equal("未命名任务", Assert.Single(result.Tasks).Title);
            Assert.DoesNotContain("private prompt body", result.Tasks.Select(item => item.Title));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DoesNotCountFutureDatedThreadInCurrentPeriods()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-sqlite-future-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "state_5.sqlite");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE threads (id TEXT PRIMARY KEY, title TEXT, tokens_used INTEGER, updated_at INTEGER);
                    INSERT INTO threads (id, title, tokens_used, updated_at)
                    VALUES ('future', 'future task', 321, $updated);
                    """;
                command.Parameters.AddWithValue("$updated", DateTimeOffset.Now.AddDays(2).ToUnixTimeSeconds());
                await command.ExecuteNonQueryAsync();
            }

            var paths = new CodexPaths(root, root, database, "missing", "missing", "missing", "missing", "missing");
            var result = await new CodexSqliteReader(paths).ReadAsync();

            Assert.Equal(321, result.Tokens.Lifetime.Tokens);
            Assert.Equal(0, result.Tokens.Today.Tokens);
            Assert.Equal(0, result.Tokens.SevenDays.Tokens);
            Assert.Equal(0, result.Tokens.Month.Tokens);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
