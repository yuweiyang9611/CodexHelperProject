using CodexU.Application;
using CodexU.Contracts;
using CodexU.Core;

namespace CodexU.Sidecar;

/// <summary>
/// Maps host-neutral quota notifications onto the explicit Electron host event.
/// The native host owns availability checks and presentation; Core owns alert
/// decisions and de-duplication.
/// </summary>
public sealed class SidecarNativeNotificationBridge(
    IIpcEventSink eventSink,
    QuotaNotificationProjector? projector = null)
{
    private readonly IIpcEventSink _eventSink =
        eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    private readonly QuotaNotificationProjector _projector = projector ?? new();

    public void Publish(DashboardSnapshot snapshot, AppSettings settings)
    {
        foreach (var notification in _projector.Project(snapshot, settings))
        {
            _eventSink.PostEvent(
                HostEventNames.ShowNativeNotification,
                new NativeNotificationRequest(
                    notification.Id,
                    notification.Title,
                    notification.Body));
        }
    }
}
