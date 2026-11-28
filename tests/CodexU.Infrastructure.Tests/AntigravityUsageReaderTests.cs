using System.Text;
using CodexU.Core;
using CodexU.Infrastructure;
using Microsoft.Data.Sqlite;

namespace CodexU.Infrastructure.Tests;

public sealed class AntigravityUsageReaderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "codexU-antigravity-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        UsageReadContext.Invalidate(_tempRoot);
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ProtobufParserReadsConfirmedFieldsIgnoresDuplicate17AndSkipsInterruptedCalls()
    {
        var call1 = BuildGenMetadataBlob(
            modelId: "gemini-4-neon",
            displayName: "Gemini 4 Neon (High)",
            input: 10_640,
            cacheRead: 0,
            output: 317,
            thinking: 271,
            visibleOutput: 46,
            responseId: "resp-1",
            includeDuplicate17: true);

        var call2 = BuildGenMetadataBlob(
            modelId: null,
            displayName: "Gemini 4 Neon (High)",
            input: 3_681,
            cacheRead: 8_583,
            output: 68,
            thinking: 20,
            visibleOutput: 48,
            responseId: "resp-2",
            includeDuplicate17: true);

        var interrupted = BuildGenMetadataBlob(
            modelId: "gemini-4-neon",
            displayName: null,
            input: null,
            cacheRead: null,
            output: null,
            thinking: null,
            visibleOutput: null,
            responseId: null,
            includeDuplicate17: false);

        Assert.True(AntigravityGenMetadataParser.TryParse(call1, out var parsed1, out var malformed1));
        Assert.False(malformed1);
        Assert.Equal("gemini-4-neon", parsed1.Model);
        Assert.Equal("resp-1", parsed1.ResponseId);
        Assert.Equal(10_640, parsed1.Tokens.InputTokens);
        Assert.Equal(0, parsed1.Tokens.CachedInputTokens);
        Assert.Equal(317, parsed1.Tokens.OutputTokens);
        Assert.Equal(271, parsed1.Tokens.ReasoningOutputTokens);
        Assert.Equal(10_957, parsed1.Tokens.TotalTokens);

        Assert.True(AntigravityGenMetadataParser.TryParse(call2, out var parsed2, out var malformed2));
        Assert.False(malformed2);
        Assert.Equal("Gemini 4 Neon (High)", parsed2.Model);
        Assert.Equal("resp-2", parsed2.ResponseId);
        Assert.Equal(3_681, parsed2.Tokens.InputTokens);
        Assert.Equal(8_583, parsed2.Tokens.CachedInputTokens);
        Assert.Equal(68, parsed2.Tokens.OutputTokens);
        Assert.Equal(20, parsed2.Tokens.ReasoningOutputTokens);
        Assert.Equal(3_749, parsed2.Tokens.TotalTokens);

        Assert.Equal(14_321, parsed1.Tokens.InputTokens + parsed2.Tokens.InputTokens);
        Assert.Equal(385, parsed1.Tokens.OutputTokens + parsed2.Tokens.OutputTokens);
        Assert.Equal(291, parsed1.Tokens.ReasoningOutputTokens + parsed2.Tokens.ReasoningOutputTokens);
        Assert.Equal(14_706, parsed1.Tokens.TotalTokens + parsed2.Tokens.TotalTokens);

        Assert.False(AntigravityGenMetadataParser.TryParse(interrupted, out _, out var malformed3));
        Assert.False(malformed3);
    }

    [Theory]
    [InlineData("claude-opus-5-5-high", "claude-opus-5-5")]
    [InlineData("claude-opus-5-5-xhigh", "claude-opus-5-5")]
    [InlineData("claude-opus-5-5", "claude-opus-5-5")]
    [InlineData("gemini-4-neon", "gemini-4-neon")]
    [InlineData("gemini-custom-high", "gemini-custom-high")]
    public void HostedClaudeModelsDropTheEffortSuffixSoBuiltInRatesApply(string stored, string expected)
    {
        var blob = BuildGenMetadataBlob(stored, null, 1_000, 0, 100, 0, 100, "resp-1", includeDuplicate17: false);

        Assert.True(AntigravityGenMetadataParser.TryParse(blob, out var parsed, out _));

        Assert.Equal(expected, parsed.Model);
        Assert.Equal(expected.StartsWith("claude-", StringComparison.Ordinal),
            UsageCredits.FindRate(parsed.Model, new DateOnly(2026, 11, 1)) is not null);
    }

    [Fact]
    public void DiscoversHostManagedAcpRootsBesideTheStandardOnes()
    {
        var home = Path.Combine(_tempRoot, "home");
        var instance = Path.Combine(home, ".t3", "userdata", "providers", "antigravity", "instance-1");
        Directory.CreateDirectory(Path.Combine(instance, "antigravity-acp"));
        Directory.CreateDirectory(Path.Combine(instance, "config"));

        var roots = AntigravityUsageReader.DiscoverRoots(home);

        Assert.Equal(
            [
                Path.Combine(home, ".gemini", "antigravity-cli"),
                Path.Combine(home, ".gemini", "antigravity"),
                Path.Combine(instance, "antigravity-acp")
            ],
            roots);
    }

    [Fact]
    public void ProtobufParserRejectsTruncatedAndInvalidWireTypesSafely()
    {
        // Truncated varint inside field 1
        byte[] truncated = [0x0A, 0x05, 0x22, 0x02, 0x10];
        Assert.False(AntigravityGenMetadataParser.TryParse(truncated, out _, out var malformedTruncated));
        Assert.True(malformedTruncated);

        // Invalid wire type 6
        byte[] invalidWireType = [0x0E, 0x01];
        Assert.False(AntigravityGenMetadataParser.TryParse(invalidWireType, out _, out var malformedWire));
        Assert.True(malformedWire);
    }

    [Fact]
    public async Task ReadsBothRootsNormalizesUrisDeduplicatesResponsesAndCachesUnchangedFiles()
    {
        var cliRoot = Path.Combine(_tempRoot, "antigravity-cli");
        var ideRoot = Path.Combine(_tempRoot, "antigravity");
        var cacheDir = Path.Combine(_tempRoot, "cache");
        var now = DateTimeOffset.UtcNow;

        CreateSummariesDb(cliRoot,
        [
            ("conv-1", "[\"file:///d%3A/Workspace/SampleRepo\"]", now.ToString("O"), null),
            ("conv-summary-only", "[\"file:///d%3A/Workspace/SampleRepo\"]", now.ToString("O"), null)
        ]);
        CreateConversationDb(cliRoot, "conv-1",
        [
            BuildGenMetadataBlob("gemini-4-neon", "Gemini 4 Neon", 10_640, 0, 317, 271, 46, "resp-shared", includeDuplicate17: true),
            BuildGenMetadataBlob("gemini-4-neon", "Gemini 4 Neon", 3_681, 8_583, 68, 20, 48, "resp-2", includeDuplicate17: false)
        ]);

        CreateSummariesDb(ideRoot,
        [
            ("conv-2", "[\"file:///d%3A/Workspace/My%20App\"]", now.ToUnixTimeMilliseconds().ToString(), "conv-1")
        ]);
        CreateConversationDb(ideRoot, "conv-2",
        [
            // Duplicate response_id with conv-1: must not be double counted.
            BuildGenMetadataBlob("gemini-4-neon", "Gemini 4 Neon", 10_640, 0, 317, 271, 46, "resp-shared", includeDuplicate17: false),
            BuildGenMetadataBlob("gemini-2.5-pro", "Gemini 2.5 Pro", 2_000, 500, 500, 100, 400, "resp-3", includeDuplicate17: false)
        ]);

        var reader = new AntigravityUsageReader(
            [cliRoot, ideRoot],
            applicationDataDirectory: cacheDir,
            incrementalIndexEnabled: true);

        var first = await reader.ReadAsync();
        Assert.Equal(DataQuality.Detailed, first.Tokens.Today.Quality);
        Assert.Equal(17_206, first.Tokens.Today.Tokens);
        Assert.Equal(17_206, first.Tokens.Today.UnratedTokens);
        Assert.Equal(0d, first.Tokens.Today.CreditsUsed);
        Assert.Equal(17_206, first.Tokens.Month.Tokens);
        Assert.Equal(2, first.Projects.Count);

        var repo = Assert.Single(first.Projects, p => p.Name == "SampleRepo");
        Assert.Equal(@"D:\Workspace\SampleRepo", repo.FullPath);
        Assert.Equal(14_706, repo.Tokens);
        Assert.Equal(ProjectCostStatus.Unknown, repo.CostStatus);
        Assert.Null(repo.CreditsUsed);

        var app = Assert.Single(first.Projects, p => p.Name == "My App");
        Assert.Equal(@"D:\Workspace\My App", app.FullPath);
        Assert.Equal(2_500, app.Tokens);

        Assert.Equal(2, first.IndexStatus.ParsedFiles);
        Assert.Equal(0, first.IndexStatus.ReusedFiles);

        var second = await reader.ReadAsync();
        Assert.Equal(17_206, second.Tokens.Today.Tokens);
        Assert.Equal(0, second.IndexStatus.ParsedFiles);
        Assert.Equal(2, second.IndexStatus.ReusedFiles);
    }

    [Fact]
    public async Task CorruptConversationDbDegradesToPartialWithoutThrowing()
    {
        var cliRoot = Path.Combine(_tempRoot, "antigravity-cli");
        var cacheDir = Path.Combine(_tempRoot, "cache");
        var now = DateTimeOffset.UtcNow;

        CreateSummariesDb(cliRoot,
        [
            ("conv-good", "[\"file:///d%3A/Workspace/SampleRepo\"]", now.ToString("O"), null),
            ("conv-bad", "[\"file:///d%3A/Workspace/SampleRepo\"]", now.ToString("O"), null)
        ]);
        CreateConversationDb(cliRoot, "conv-good",
        [
            BuildGenMetadataBlob("gemini-4-neon", null, 1_000, 0, 200, 50, 150, "resp-good", includeDuplicate17: false)
        ]);

        var badDbPath = Path.Combine(cliRoot, "conversations", "conv-bad.db");
        await File.WriteAllTextAsync(badDbPath, "not-a-sqlite-database");

        var reader = new AntigravityUsageReader([cliRoot], applicationDataDirectory: cacheDir);
        var snapshot = await reader.ReadAsync();

        Assert.Equal(DataQuality.Partial, snapshot.Tokens.Today.Quality);
        Assert.Equal(1_200, snapshot.Tokens.Today.Tokens);
        Assert.Contains(snapshot.Diagnostics, d => d.Contains("部分数据", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingRootsReturnUnavailableEmptySnapshot()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        var reader = new AntigravityUsageReader([missing], applicationDataDirectory: _tempRoot);
        var snapshot = await reader.ReadAsync();

        Assert.Equal(DataQuality.Unavailable, snapshot.Tokens.Month.Quality);
        Assert.Equal(0, snapshot.Tokens.Lifetime.Tokens);
        Assert.Empty(snapshot.Projects);
    }

    private static void CreateSummariesDb(
        string root,
        IReadOnlyList<(string Id, string WorkspaceUris, string LastModified, string? ParentId)> rows)
    {
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "conversation_summaries.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE conversation_summaries (
                conversation_id TEXT PRIMARY KEY,
                workspace_uris TEXT,
                last_modified_time TEXT,
                step_count INTEGER,
                parent_conversation_id TEXT
            );
            """;
        create.ExecuteNonQuery();

        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversation_summaries
                    (conversation_id, workspace_uris, last_modified_time, step_count, parent_conversation_id)
                VALUES ($id, $uris, $modified, 1, $parent);
                """;
            insert.Parameters.AddWithValue("$id", row.Id);
            insert.Parameters.AddWithValue("$uris", row.WorkspaceUris);
            insert.Parameters.AddWithValue("$modified", row.LastModified);
            insert.Parameters.AddWithValue("$parent", (object?)row.ParentId ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }
    }

    private static void CreateConversationDb(string root, string conversationId, IReadOnlyList<byte[]> blobs)
    {
        var dir = Path.Combine(root, "conversations");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, conversationId + ".db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB);";
        create.ExecuteNonQuery();

        for (var i = 0; i < blobs.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES ($idx, $data);";
            insert.Parameters.AddWithValue("$idx", i);
            insert.Parameters.AddWithValue("$data", blobs[i]);
            insert.ExecuteNonQuery();
        }
    }

    private static byte[] BuildGenMetadataBlob(
        string? modelId,
        string? displayName,
        long? input,
        long? cacheRead,
        long? output,
        long? thinking,
        long? visibleOutput,
        string? responseId,
        bool includeDuplicate17)
    {
        var inner = new List<byte>();
        if (modelId is not null) WriteStringField(inner, 19, modelId);
        if (displayName is not null) WriteStringField(inner, 21, displayName);

        if (input is not null || output is not null)
        {
            var usage = new List<byte>();
            if (input is not null) WriteVarintField(usage, 2, (ulong)input.Value);
            if (output is not null) WriteVarintField(usage, 3, (ulong)output.Value);
            if (cacheRead is not null) WriteVarintField(usage, 5, (ulong)cacheRead.Value);
            if (responseId is not null) WriteStringField(usage, 7, responseId);
            if (thinking is not null) WriteVarintField(usage, 9, (ulong)thinking.Value);
            if (visibleOutput is not null) WriteVarintField(usage, 10, (ulong)visibleOutput.Value);
            WriteBytesField(inner, 4, usage);

            if (includeDuplicate17)
            {
                var dupWrapper = new List<byte>();
                WriteBytesField(dupWrapper, 2, usage);
                WriteBytesField(inner, 17, dupWrapper);
            }
        }

        // Field 20 repeated key/value metadata
        var kv = new List<byte>();
        WriteStringField(kv, 1, "trajectory_source");
        WriteStringField(kv, 2, "cli");
        WriteBytesField(inner, 20, kv);

        var root = new List<byte>();
        WriteBytesField(root, 1, inner);
        return [.. root];
    }

    private static void WriteVarintField(List<byte> buffer, int fieldNumber, ulong value)
    {
        WriteVarint(buffer, ((ulong)fieldNumber << 3) | 0UL);
        WriteVarint(buffer, value);
    }

    private static void WriteStringField(List<byte> buffer, int fieldNumber, string value) =>
        WriteBytesField(buffer, fieldNumber, Encoding.UTF8.GetBytes(value));

    private static void WriteBytesField(List<byte> buffer, int fieldNumber, IReadOnlyList<byte> payload)
    {
        WriteVarint(buffer, ((ulong)fieldNumber << 3) | 2UL);
        WriteVarint(buffer, (ulong)payload.Count);
        buffer.AddRange(payload);
    }

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buffer.Add((byte)value);
    }
}
