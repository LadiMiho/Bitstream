using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.Notifications;

/// <summary>
/// How request statuses read to people: on the portal's pages (Web's ActivationStatusLabels and
/// PostActivationRequestLabels use these) and in the notification emails, so both always say the
/// same thing. <c>wwwroot/js/status-presentation.js</c> holds the same activation wording for the grid.
/// </summary>
public static class RequestStatusText
{
    public static string Activation(ActivationRequestStatus status) => status switch
    {
        ActivationRequestStatus.Submitted => "Submitted",
        ActivationRequestStatus.PendingCrmSync => "Pending CRM Sync",
        ActivationRequestStatus.AwaitingGisVerification => "Waiting GIS Verification",
        ActivationRequestStatus.RejectedNoLine => "Rejected — No Line",
        ActivationRequestStatus.LineAvailable => "Line Available",
        ActivationRequestStatus.SalesOrderOpened => "Activation in progress",
        ActivationRequestStatus.InProvisioning => "In Provisioning",
        ActivationRequestStatus.Closed => "Closed",
        ActivationRequestStatus.Completed => "Completed",
        ActivationRequestStatus.IntegrationFailed => "Integration Failed",
        ActivationRequestStatus.AwaitingOperatorConfirmation => "Waiting for operator confirmation",
        ActivationRequestStatus.WaitingForServiceDesk => "Waiting for service desk",
        ActivationRequestStatus.ActivationFailed => "Activation failed",
        _ => status.ToString()
    };

    public static string PostActivation(string status) => status switch
    {
        PostActivationRequestStatus.PendingCrmSync => "Pending CRM sync",
        PostActivationRequestStatus.InProcess => "In process",
        PostActivationRequestStatus.TechnicallyCompleted => "Technically completed",
        PostActivationRequestStatus.Submitted => "Submitted to CRM",
        PostActivationRequestStatus.AlreadyExists => "Already exists in CRM",
        PostActivationRequestStatus.Failed => "Failed",
        _ => status
    };

    /// <summary>The label of a status stored as text on a notification (<see cref="NotificationEntityTypes"/>).</summary>
    public static string For(string entityType, string status) => entityType switch
    {
        NotificationEntityTypes.ActivationRequest when Enum.TryParse<ActivationRequestStatus>(status, out var parsed) => Activation(parsed),
        NotificationEntityTypes.PostActivationRequest => PostActivation(status),
        _ => status
    };

    /// <summary>"Activation request" / "Post-activation request".</summary>
    public static string RequestType(string entityType) => entityType switch
    {
        NotificationEntityTypes.ActivationRequest => "Activation request",
        NotificationEntityTypes.PostActivationRequest => "Post-activation request",
        _ => "Request"
    };

    /// <summary>The portal path that shows the request with its details drawer open.</summary>
    public static string RequestPath(string entityType, string publicId) => entityType switch
    {
        NotificationEntityTypes.ActivationRequest => $"/ActivationRequests?open={Uri.EscapeDataString(publicId)}",
        NotificationEntityTypes.PostActivationRequest => $"/PostActivation?open={Uri.EscapeDataString(publicId)}",
        _ => "/Notifications"
    };
}
