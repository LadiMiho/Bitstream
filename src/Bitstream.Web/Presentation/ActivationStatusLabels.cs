using Bitstream.Domain.Enums;

namespace Bitstream.Web.Presentation;

/// <summary>
/// Display labels for <see cref="ActivationRequestStatus"/> in server-rendered drawers — the same
/// wording as <c>wwwroot/js/status-presentation.js</c> uses in the grid.
/// </summary>
public static class ActivationStatusLabels
{
    public static string For(ActivationRequestStatus status) => status switch
    {
        ActivationRequestStatus.Submitted => "Submitted",
        ActivationRequestStatus.PendingCrmSync => "Pending CRM Sync",
        ActivationRequestStatus.AwaitingGisVerification => "Awaiting GIS Verification",
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
}
