namespace CodexU.Sidecar.Tests;

public sealed class SidecarVersionTests
{
    [Fact]
    public void NormalizePreservesPrereleaseAndRemovesBuildMetadata()
    {
        var version = SidecarVersion.Normalize(
            " 0.6.0-beta.1+ca08992178ce ",
            new Version(0, 6, 0, 0));

        Assert.Equal("0.6.0-beta.1", version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+ca08992178ce")]
    public void NormalizeFallsBackToAssemblyVersion(string? informationalVersion)
    {
        var version = SidecarVersion.Normalize(
            informationalVersion,
            new Version(0, 6, 0, 0));

        Assert.Equal("0.6.0", version);
    }

    [Fact]
    public void NormalizeFallsBackToDevelopmentWithoutVersionMetadata()
    {
        Assert.Equal("development", SidecarVersion.Normalize(null, null));
    }
}
