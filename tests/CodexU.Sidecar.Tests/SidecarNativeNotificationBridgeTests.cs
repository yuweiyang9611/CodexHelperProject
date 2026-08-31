using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;

namespace CodexU.Sidecar.Tests;

public sealed class SidecarNativeNotificationBridgeTests
{
    [Fact]
    public void Publish_EmitsTheExplicitNativeHostEventOnce()
    {
        var events = new RecordingEventSink();
        var bridge = new SidecarNativeNotificationBridge(
            events,
            new QuotaNotificationProjector(
                () => new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero)));
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex) with
        {
            PrimaryQuota = new RateLimitWindow(95, 300, null)
        };
        var settings = new AppSettings(
            NotificationsEnabled: true,
            FiveHourAlertPercent: 20,
            MinimumRateCoverageAlertPercent: 0);

        bridge.Publish(snapshot, settings);
        bridge.Publish(snapshot, settings);

        var hostEvent = Assert.Single(events.Events);
        Assert.Equal(HostEventNames.ShowNativeNotification, hostEvent.Method);
        var request = Assert.IsType<NativeNotificationRequest>(hostEvent.Payload);
        Assert.StartsWith("quota-below-threshold:", request.Id);
        Assert.Equal("codexU 额度提醒", request.Title);
        Assert.Contains("额度剩余 5%", request.Body);
    }

    [Fact]
    public void Publish_EmitsNothingWhenNotificationsAreDisabled()
    {
        var events = new RecordingEventSink();
        var bridge = new SidecarNativeNotificationBridge(events);
        var snapshot = DashboardSnapshot.Empty(AgentRuntime.Codex) with
        {
            PrimaryQuota = new RateLimitWindow(99, 300, null)
        };

        bridge.Publish(
            snapshot,
            new AppSettings(NotificationsEnabled: false));

        Assert.Empty(events.Events);
    }

    private sealed class RecordingEventSink : IIpcEventSink
    {
        public List<RecordedEvent> Events { get; } = [];

        public void PostEvent(string method, object? payload) =>
            Events.Add(new RecordedEvent(method, payload));
    }

    private sealed record RecordedEvent(string Method, object? Payload);
}
