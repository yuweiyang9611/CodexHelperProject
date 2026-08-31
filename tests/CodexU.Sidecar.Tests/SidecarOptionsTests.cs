using CodexU.Contracts;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarOptionsTests
{
    [Fact]
    public void Parse_EnablesNativeNotificationsOnlyForTheExplicitHostSignal()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"codexu-sidecar-options-{Guid.NewGuid():N}");

        var unavailable = SidecarOptions.Parse(
            ["--data-directory", dataDirectory, "--app-version", "test"]);
        var available = SidecarOptions.Parse(
            [
                "--data-directory",
                dataDirectory,
                "--app-version",
                "test",
                "--native-notifications"
            ]);

        Assert.False(unavailable.NativeNotificationsAvailable);
        Assert.True(available.NativeNotificationsAvailable);
        Assert.DoesNotContain(
            HostCapabilityNames.NativeNotifications,
            SidecarOptions.ResolveHostCapabilities(
                unavailable.Platform,
                unavailable.IsPackaged,
                unavailable.NativeNotificationsAvailable));
        Assert.Contains(
            HostCapabilityNames.NativeNotifications,
            SidecarOptions.ResolveHostCapabilities(
                available.Platform,
                available.IsPackaged,
                available.NativeNotificationsAvailable));
    }
}
