using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Web.Presentation;

/// <summary>
/// How a <see cref="UserNotification"/> reads on screen. Notifications store the status codes,
/// not text, so they are always worded with the current labels.
/// </summary>
public static class NotificationPresenter
{
    public static string Title(UserNotification notification) => notification.EntityType switch
    {
        NotificationEntityTypes.ActivationRequest => $"Activation request {notification.EntityPublicId}",
        NotificationEntityTypes.PostActivationRequest => $"Post-activation request {notification.EntityPublicId}",
        _ => notification.EntityPublicId
    };

    /// <summary>"Status changed from A to B", or "New request: B" for a request that was just created.</summary>
    public static string Message(UserNotification notification) => notification.PreviousStatus is null
        ? $"New request: {StatusLabel(notification.EntityType, notification.Status)}"
        : $"Status changed from {StatusLabel(notification.EntityType, notification.PreviousStatus)} to {StatusLabel(notification.EntityType, notification.Status)}";

    public static string StatusLabel(string entityType, string status) => entityType switch
    {
        NotificationEntityTypes.ActivationRequest when Enum.TryParse<ActivationRequestStatus>(status, out var parsed) => ActivationStatusLabels.For(parsed),
        NotificationEntityTypes.PostActivationRequest => PostActivationRequestLabels.For(status),
        _ => status
    };

    public static string StatusPillClass(string entityType, string status) => entityType switch
    {
        NotificationEntityTypes.ActivationRequest when Enum.TryParse<ActivationRequestStatus>(status, out var parsed) => ActivationStatusLabels.PillClass(parsed),
        NotificationEntityTypes.PostActivationRequest => PostActivationRequestLabels.PillClass(status),
        _ => "status-pill bg-surface-muted text-ink-muted"
    };

    /// <summary>The screen that shows the request, with its details drawer opened.</summary>
    public static string TargetUrl(UserNotification notification) => notification.EntityType switch
    {
        NotificationEntityTypes.ActivationRequest => $"/ActivationRequests?open={Uri.EscapeDataString(notification.EntityPublicId)}",
        NotificationEntityTypes.PostActivationRequest => $"/PostActivation?open={Uri.EscapeDataString(notification.EntityPublicId)}",
        _ => "/Notifications"
    };

    /// <summary>The link a notification is opened through: it is marked read, then the request is shown.</summary>
    public static string OpenUrl(UserNotification notification) => $"/Notifications/{notification.NotificationId}/Open";
}
