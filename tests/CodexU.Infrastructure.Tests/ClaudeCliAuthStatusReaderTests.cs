using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class ClaudeCliAuthStatusReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.ClaudeAi)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"api_key\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.ApiKey)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"api_key_helper\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.ApiKeyHelper)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"oauth_token\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.OauthToken)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"third_party\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.ThirdParty)]
    [InlineData(1, "{\"loggedIn\":false,\"authMethod\":\"none\"}", ClaudeCliLoginStatus.LoggedOut, ClaudeCliAuthMethod.None)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"future-method\"}", ClaudeCliLoginStatus.LoggedIn, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "{\"loggedIn\":false,\"authMethod\":\"none\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(1, "{\"loggedIn\":true,\"authMethod\":\"claude.ai\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"none\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(2, "{\"loggedIn\":false,\"authMethod\":\"none\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "Not logged in", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "{\"loggedIn\":\"true\",\"authMethod\":\"claude.ai\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "{\"loggedIn\":true,\"loggedIn\":false,\"authMethod\":\"none\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    [InlineData(0, "{\"loggedIn\":true,\"authMethod\":\"none\",\"authMethod\":\"claude.ai\"}", ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown)]
    public void ParsesOnlyAuthoritativeLoginState(int exitCode, string output, ClaudeCliLoginStatus expected, ClaudeCliAuthMethod method)
    {
        var result = ClaudeCliAuthStatusReader.Parse(new(exitCode, output), ClaudeCliSource.Standalone, Now);
        Assert.Equal(expected, result.Status);
        Assert.Equal(method, result.AuthMethod);
        Assert.Equal(Now, result.CheckedAt);
    }

    [Fact]
    public void ReturnsNoRawAccountFieldsOrDiagnosticOutput()
    {
        var result = ClaudeCliAuthStatusReader.Parse(new(0,
            """{"loggedIn":true,"authMethod":"claude.ai","email":"private@example.test","orgId":"private-org","configDirectory":"private-path"}"""),
            ClaudeCliSource.DesktopEmbedded, Now);
        Assert.Equal(ClaudeCliLoginStatus.LoggedIn, result.Status);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        var invalid = ClaudeCliAuthStatusReader.Parse(new(1, new string('x', 65 * 1024)), ClaudeCliSource.Path, Now);
        Assert.Equal(ClaudeCliLoginStatus.Unavailable, invalid.Status);
    }

    [Fact]
    public void DiscoversStandaloneThenNativePathThenNewestDesktopVersion()
    {
        using var fixture = new Fixture();
        var nativeName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var oldest = fixture.Touch("local/Claude-Data/claude_fixture/claude-code/2.9.9/hash/" + nativeName);
        var newest = fixture.Touch("local/Claude-Data/claude_fixture/claude-code/2.10.0/hash/" + nativeName);
        var found = ClaudeCliAuthStatusReader.Discover(fixture.Root, fixture.Local, "relative");
        Assert.Equal(newest, found?.Path);
        Assert.Equal(ClaudeCliSource.DesktopEmbedded, found?.Source);
        var path = fixture.Touch("path/" + nativeName);
        found = ClaudeCliAuthStatusReader.Discover(fixture.Root, fixture.Local, Path.GetDirectoryName(path));
        Assert.Equal(path, found?.Path);
        Assert.Equal(ClaudeCliSource.Path, found?.Source);
        var standalone = fixture.Touch(".local/bin/" + nativeName);
        found = ClaudeCliAuthStatusReader.Discover(fixture.Root, fixture.Local, Path.GetDirectoryName(path));
        Assert.Equal(standalone, found?.Path);
        Assert.Equal(ClaudeCliSource.Standalone, found?.Source);
        Assert.NotEqual(oldest, found?.Path);
    }

    [Fact]
    public async Task MissingExecutableIsDistinctFromACommandFailure()
    {
        var noCli = new ClaudeCliAuthStatusReader(() => null, (_, _) => throw new Exception("must not execute"), new FixedClock(), TimeSpan.FromSeconds(1));
        Assert.Equal(ClaudeCliLoginStatus.NotInstalled, (await noCli.ReadAsync()).Status);
        var failed = new ClaudeCliAuthStatusReader(() => new("fixture", ClaudeCliSource.Standalone),
            (_, _) => throw new IOException("private-path"), new FixedClock(), TimeSpan.FromSeconds(1));
        var status = await failed.ReadAsync();
        Assert.Equal(ClaudeCliLoginStatus.Unavailable, status.Status);
        Assert.DoesNotContain("private-path", JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchesHiddenStatusCommandWithoutAShellOrInteractiveInput()
    {
        var info = ClaudeCliAuthStatusReader.StartInfo("fixture");
        Assert.Equal(new[] { "auth", "status" }, info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.RedirectStandardInput);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
    }

    [Fact]
    public async Task RejectsBoundedOutputOverflow()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 4097)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ClaudeCliAuthStatusReader.ReadBoundedAsync(stream, 4096, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealProcessIsKilledOnDeadlineOrCallerCancellation(bool callerCancels)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var pidFile = Path.Combine(fixture.Root, "pid.txt");
        var processInfo = ClaudeCliAuthStatusReader.StartInfo("powershell.exe");
        processInfo.ArgumentList.Clear();
        processInfo.ArgumentList.Add("-NoProfile");
        processInfo.ArgumentList.Add("-NonInteractive");
        processInfo.ArgumentList.Add("-Command");
        processInfo.ArgumentList.Add($"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$PID); Start-Sleep -Seconds 30");
        var reader = new ClaudeCliAuthStatusReader(() => new("fixture", ClaudeCliSource.Standalone),
            (_, token) => ClaudeCliAuthStatusReader.RunProcessAsync(processInfo, token), new FixedClock(), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var pending = reader.ReadAsync(cancellation.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(pidFile) && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(File.Exists(pidFile), "Synthetic child must start before testing cleanup.");
        var id = int.Parse(await File.ReadAllTextAsync(pidFile));
        if (callerCancels)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else Assert.Equal(ClaudeCliLoginStatus.Unavailable, (await pending).Status);
        try { Assert.True(Process.GetProcessById(id).HasExited); }
        catch (ArgumentException) { /* The terminated child has already been reaped. */ }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codexu-cli-status-" + Guid.NewGuid().ToString("N"));
        public string Local => Path.Combine(Root, "local");
        public Fixture() => Directory.CreateDirectory(Root);
        public string Touch(string relative)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
