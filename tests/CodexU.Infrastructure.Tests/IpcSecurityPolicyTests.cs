using System.Text.Json;
using CodexU.Contracts;

namespace CodexU.Infrastructure.Tests;

public sealed class IpcSecurityPolicyTests
{
    [Theory]
    [InlineData("https://app.codexu.local/index.html#/dashboard", true)]
    [InlineData("https://APP.CODEXU.LOCAL/", true)]
    [InlineData("http://app.codexu.local/", false)]
    [InlineData("https://app.codexu.local.evil.example/", false)]
    [InlineData("https://app.codexu.local:444/", false)]
    [InlineData("not-a-uri", false)]
    public void IsTrustedSource_RequiresExactVirtualHttpsOrigin(string source, bool expected)
    {
        Assert.Equal(expected, IpcSecurityPolicy.IsTrustedSource(source));
    }

    [Fact]
    public void IsValidRequest_RejectsUnsupportedProtocolVersion()
    {
        using var payload = JsonDocument.Parse("{}");
        var request = new IpcRequest(2, "id", "request", "usage.refresh", payload.RootElement.Clone());

        Assert.False(IpcSecurityPolicy.IsValidRequest(request));
    }

    [Fact]
    public void IsValidRequest_RejectsOversizedIdentifiers()
    {
        using var payload = JsonDocument.Parse("{}");
        var request = new IpcRequest(1, new string('x', 129), "request", "usage.refresh", payload.RootElement.Clone());

        Assert.False(IpcSecurityPolicy.IsValidRequest(request));
    }

    [Fact]
    public void IsAllowedMethod_AllowsDeclaredCapabilitiesAndRejectsArbitraryHostAccess()
    {
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("app.ready"));
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("usage.refresh"));
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("update.check"));
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("data.backup"));
        Assert.True(IpcSecurityPolicy.IsAllowedMethod("diagnostics.export"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("settings.reconcileStartupRegistration"));
        Assert.True(ElectronHostIpcSecurityPolicy.IsAllowedMethod(
            "settings.reconcileStartupRegistration"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("shell.execute"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("file.read"));
        Assert.False(IpcSecurityPolicy.IsAllowedMethod("process.start"));
    }
}
