using CodexU.Core;

namespace CodexU.Core.Tests;

public sealed class SmokeTestDataRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Local\\codexU-smoke-0123456789abcdef")]
    [InlineData("Global\\codexU-smoke-0123456789abcdef0123456789abcdef")]
    [InlineData("Local\\codexU-smoke-0123456789abcdef0123456789abcdeg")]
    public void IsValidReadyEventName_RejectsMalformedNames(string? eventName)
    {
        Assert.False(SmokeTestDataRoot.IsValidReadyEventName(eventName));
    }

    [Fact]
    public void TryResolve_AcceptsExistingCanonicalDirectoryWithMatchingNonce()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), $"{SmokeTestDataRoot.DirectoryPrefix}{nonce}");
        Directory.CreateDirectory(directory);

        try
        {
            var resolved = SmokeTestDataRoot.TryResolve(
                $"{SmokeTestDataRoot.ReadyEventPrefix}{nonce}",
                directory,
                Path.GetTempPath(),
                out var applicationDataDirectory);

            Assert.True(resolved);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
                applicationDataDirectory,
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryResolve_RejectsMissingMismatchedAndNonCanonicalDirectories()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var otherNonce = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), $"{SmokeTestDataRoot.DirectoryPrefix}{nonce}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.False(SmokeTestDataRoot.TryResolve(
                $"{SmokeTestDataRoot.ReadyEventPrefix}{nonce}",
                null,
                Path.GetTempPath(),
                out _));
            Assert.False(SmokeTestDataRoot.TryResolve(
                $"{SmokeTestDataRoot.ReadyEventPrefix}{otherNonce}",
                directory,
                Path.GetTempPath(),
                out _));
            Assert.False(SmokeTestDataRoot.TryResolve(
                $"{SmokeTestDataRoot.ReadyEventPrefix}{nonce}",
                Path.Combine(directory, "..", Path.GetFileName(directory)),
                Path.GetTempPath(),
                out _));

            Directory.Delete(directory);
            Assert.False(SmokeTestDataRoot.TryResolve(
                $"{SmokeTestDataRoot.ReadyEventPrefix}{nonce}",
                directory,
                Path.GetTempPath(),
                out _));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
