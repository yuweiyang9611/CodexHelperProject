using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class CodexPathsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Discover_DefaultClaudeSourceEnablesDesktopHistory(string? overrideDirectory)
    {
        var home = Path.Combine(Path.GetTempPath(), "codexu-path-home");
        var local = Path.Combine(home, "AppData", "Local");
        var paths = CodexPaths.Discover(home, local, null, null, overrideDirectory);
        Assert.Equal(Path.Combine(home, ".claude"), paths.ClaudeDirectory);
        Assert.Equal(Path.Combine(local, "Claude-Data"), paths.ClaudeDesktopDataDirectory);
    }

    [Fact]
    public void Discover_ExplicitClaudeSourceDoesNotMergeTheDesktopAccount()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexu-path-home");
        var local = Path.Combine(home, "AppData", "Local");
        var alternate = Path.Combine(home, "isolated-claude");
        var paths = CodexPaths.Discover(home, local, null, null, alternate);
        Assert.Equal(alternate, paths.ClaudeDirectory);
        Assert.Null(paths.ClaudeDesktopDataDirectory);
    }

    [Fact]
    public void Discover_InvalidExplicitClaudeSourceStillDoesNotGuessDesktopIdentity()
    {
        var home = Path.Combine(Path.GetTempPath(), "codexu-path-home");
        var local = Path.Combine(home, "AppData", "Local");
        var paths = CodexPaths.Discover(home, local, null, null, "invalid\0path");
        Assert.Equal(Path.Combine(home, ".claude"), paths.ClaudeDirectory);
        Assert.Null(paths.ClaudeDesktopDataDirectory);
    }
}
