using System.Text.Json;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexAppServerClientTests
{
    [Fact]
    public void ParseAccount_ReadsSupportedProtocolShape()
    {
        using var document = JsonDocument.Parse(
            """{"account":{"type":"chatgpt","planType":"pro","email":"person@example.com"}}""");

        var account = CodexAppServerClient.ParseAccount(document.RootElement);

        Assert.NotNull(account);
        Assert.True(account.IsAuthenticated);
        Assert.Equal("pro", account.PlanType);
        Assert.Equal("person@example.com", account.Email);
    }

    [Fact]
    public void ParseRateLimits_SeparatesPrimaryAndSecondaryWindows()
    {
        var primaryReset = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var secondaryReset = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            rateLimits = new
            {
                primary = new { usedPercent = 25, windowDurationMins = 300, resetsAt = primaryReset },
                secondary = new { usedPercent = 8, windowDurationMins = 10080, resetsAt = secondaryReset }
            }
        }));

        var (primary, secondary) = CodexAppServerClient.ParseRateLimits(document.RootElement);

        Assert.Equal(25, primary?.UsedPercent);
        Assert.Equal(300, primary?.WindowDurationMinutes);
        Assert.Equal(8, secondary?.UsedPercent);
        Assert.Equal(10080, secondary?.WindowDurationMinutes);
    }

    [Fact]
    public void BuildSnapshotRequests_ContainsOnlyAccountAndRateLimits()
    {
        var methods = CodexAppServerClient.BuildSnapshotRequests()
            .Select(request =>
            {
                using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
                return document.RootElement.GetProperty("method").GetString() ?? string.Empty;
            })
            .ToArray();

        Assert.Equal(["account/read", "account/rateLimits/read"], methods);
        Assert.DoesNotContain("account/usage/read", methods);
    }

    [Fact]
    public void BuildSnapshotRequests_DisablesAccountRefresh()
    {
        var accountRequest = CodexAppServerClient.BuildSnapshotRequests()[0];
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(accountRequest));

        Assert.False(document.RootElement
            .GetProperty("params")
            .GetProperty("refreshToken")
            .GetBoolean());
    }

    [Fact]
    public void FormatAppServerError_ProvidesActionForMissingCliAuthentication()
    {
        var diagnostic = CodexAppServerClient.FormatAppServerError(
            3,
            "codex account authentication required to read rate limits");

        Assert.Contains("codex login status", diagnostic, StringComparison.Ordinal);
        Assert.Contains("codex login", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("app-server 3", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FindCandidates_DiscoversNewestDesktopRuntimeWithoutPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var older = CreateExecutable(root, "OpenAI", "Codex", "bin", "old", "codex.exe");
            var newer = CreateExecutable(root, "OpenAI", "Codex", "bin", "new", "codex.exe");
            File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

            var candidates = CodexExecutableLocator.FindCandidates(
                configuredExecutable: null,
                environmentExecutable: null,
                installDirectory: null,
                localApplicationData: root,
                pathValue: null);

            Assert.Equal(Path.GetFullPath(newer), candidates[0]);
            Assert.Equal(Path.GetFullPath(older), candidates[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindCandidates_PrefersNewestVersionedRuntimeOverRootCopy()
    {
        var root = CreateTempDirectory();
        try
        {
            var rootCopy = CreateExecutable(root, "OpenAI", "Codex", "bin", "codex.exe");
            var olderVersioned = CreateExecutable(root, "OpenAI", "Codex", "bin", "older", "codex.exe");
            var newerVersioned = CreateExecutable(root, "OpenAI", "Codex", "bin", "newer", "codex.exe");
            File.SetLastWriteTimeUtc(rootCopy, DateTime.UtcNow);
            File.SetLastWriteTimeUtc(olderVersioned, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(newerVersioned, DateTime.UtcNow.AddDays(-1));

            var candidates = CodexExecutableLocator.FindCandidates(
                configuredExecutable: null,
                environmentExecutable: null,
                installDirectory: null,
                localApplicationData: root,
                pathValue: null);

            Assert.Equal(Path.GetFullPath(newerVersioned), candidates[0]);
            Assert.Equal(Path.GetFullPath(rootCopy), candidates[1]);
            Assert.Equal(Path.GetFullPath(olderVersioned), candidates[2]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindCandidates_PrefersExplicitPathsAndSupportsRenamedDesktopDirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var configured = CreateExecutable(root, "configured", "codex.exe");
            var environment = CreateExecutable(root, "environment", "codex.exe");
            var install = CreateExecutable(root, "standalone", "codex.exe");
            var renamedDesktop = CreateExecutable(root, "OpenAI", "ChatGPT", "bin", "current", "codex.exe");
            var pathDirectory = Path.Combine(root, "path");
            var pathExecutable = CreateExecutable(pathDirectory, "codex.cmd");

            var candidates = CodexExecutableLocator.FindCandidates(
                configured,
                environment,
                Path.GetDirectoryName(install),
                root,
                pathDirectory);

            Assert.Equal(Path.GetFullPath(configured), candidates[0]);
            Assert.Equal(Path.GetFullPath(environment), candidates[1]);
            Assert.Equal(Path.GetFullPath(install), candidates[2]);
            Assert.Contains(Path.GetFullPath(renamedDesktop), candidates);
            Assert.Contains(Path.GetFullPath(pathExecutable), candidates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartFirstAvailableAsync_FallsBackUntilInitializeHandshakeSucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = CreateTempDirectory();
        try
        {
            var exitsImmediately = CreateCommandScript(
                root,
                "exits.cmd",
                "@echo off",
                "exit /b 1");
            var initializes = CreateCommandScript(
                root,
                "initializes.cmd",
                "@echo off",
                "set /p request=",
                "echo {\"id\":1,\"result\":{}}",
                "set /p notification=");
            var diagnostics = new List<string>();

            using var process = await CodexAppServerClient.StartFirstAvailableAsync(
                [exitsImmediately, initializes],
                diagnostics,
                CancellationToken.None);

            Assert.NotNull(process);
            Assert.Contains(Path.GetFullPath(initializes), process.StartInfo.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(diagnostics);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-cli-locator-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateExecutable(string root, params string[] segments)
    {
        var path = segments.Aggregate(root, Path.Combine);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    private static string CreateCommandScript(string root, string fileName, params string[] lines)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllLines(path, lines);
        return path;
    }

}
