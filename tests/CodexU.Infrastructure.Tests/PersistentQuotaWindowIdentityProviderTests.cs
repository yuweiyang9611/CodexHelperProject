using System.Text.Json;
using CodexU.Core;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class PersistentQuotaWindowIdentityProviderTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ObserveWindow_PreservesAnIdentityAcrossProviderRestarts()
    {
        var root = CreateRoot();
        try
        {
            var firstProvider = new PersistentQuotaWindowIdentityProvider(root);
            var first = firstProvider.ObserveWindow(
                AgentRuntime.ClaudeCode,
                "5 小时",
                Window(remainingPercent: 10),
                Now);

            var restartedProvider = new PersistentQuotaWindowIdentityProvider(root);
            var afterRestart = restartedProvider.ObserveWindow(
                AgentRuntime.ClaudeCode,
                "5 小时",
                Window(remainingPercent: 5),
                Now.AddMinutes(1));

            Assert.Equal(first, afterRestart);
            Assert.True(File.Exists(firstProvider.StatePath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ObserveWindow_AllocatesANewIdentityForRolloverInsideTheSameUtcBucket()
    {
        var root = CreateRoot();
        try
        {
            var firstProvider = new PersistentQuotaWindowIdentityProvider(root);
            var firstWindow = firstProvider.ObserveWindow(
                AgentRuntime.ClaudeCode,
                "5 小时",
                Window(remainingPercent: 5),
                Now);

            var restartedAtRollover = new PersistentQuotaWindowIdentityProvider(root);
            var nextWindow = restartedAtRollover.ObserveWindow(
                AgentRuntime.ClaudeCode,
                "5 小时",
                Window(remainingPercent: 98),
                Now.AddMinutes(1));
            var afterAnotherRestart = new PersistentQuotaWindowIdentityProvider(root)
                .ObserveWindow(
                    AgentRuntime.ClaudeCode,
                    "5 小时",
                    Window(remainingPercent: 10),
                    Now.AddMinutes(2));

            Assert.NotEqual(firstWindow, nextWindow);
            Assert.Equal(nextWindow, afterAnotherRestart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ObserveWindow_ReplacesMalformedStateWithoutFailingRefresh()
    {
        var root = CreateRoot();
        try
        {
            var path = new PersistentQuotaWindowIdentityProvider(root).StatePath;
            File.WriteAllText(path, "{ malformed");

            var provider = new PersistentQuotaWindowIdentityProvider(root);
            var exception = Record.Exception(() => provider.ObserveWindow(
                AgentRuntime.Codex,
                "5 小时",
                Window(remainingPercent: 25),
                Now));

            Assert.Null(exception);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ObserveWindow_ReplacesOversizedStateWithoutReadingPastTheLimit()
    {
        var root = CreateRoot();
        try
        {
            var path = new PersistentQuotaWindowIdentityProvider(root).StatePath;
            File.WriteAllBytes(path, new byte[(64 * 1024) + 1]);

            var provider = new PersistentQuotaWindowIdentityProvider(root);
            var identity = provider.ObserveWindow(
                AgentRuntime.Codex,
                "5 小时",
                Window(remainingPercent: 25),
                Now);

            Assert.Contains("|window:", identity, StringComparison.Ordinal);
            Assert.InRange(new FileInfo(path).Length, 1, 64 * 1024);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ObserveWindow_BoundsPersistedEntriesAndFileSize()
    {
        var root = CreateRoot();
        try
        {
            var provider = new PersistentQuotaWindowIdentityProvider(root);
            for (var index = 0; index < 40; index++)
            {
                provider.ObserveWindow(
                    AgentRuntime.Codex,
                    $"window-{index}",
                    Window(remainingPercent: 50),
                    Now.AddMinutes(index));
            }

            using var document = JsonDocument.Parse(File.ReadAllText(provider.StatePath));
            Assert.Equal(32, document.RootElement.GetProperty("windows").GetArrayLength());
            Assert.InRange(new FileInfo(provider.StatePath).Length, 1, 64 * 1024);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RateLimitWindow Window(double remainingPercent) =>
        new(100d - remainingPercent, 300, ResetsAt: null);

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"codexu-quota-notification-windows-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
