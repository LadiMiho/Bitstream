using Bitstream.Application.Services.Notifications;
using Bitstream.Domain.Enums;

namespace Bitstream.Web.Presentation;

/// <summary>
/// Display labels for <see cref="ActivationRequestStatus"/> in server-rendered drawers — the same
/// wording as <c>wwwroot/js/status-presentation.js</c> uses in the grid.
/// </summary>
public static class ActivationStatusLabels
{
    public static string For(ActivationRequestStatus status) => RequestStatusText.Activation(status);

    /// <summary>The status-pill colour classes, same tones as <c>status-presentation.js</c>.</summary>
    public static string PillClass(ActivationRequestStatus status) => "status-pill " + status switch
    {
        ActivationRequestStatus.PendingCrmSync
            or ActivationRequestStatus.AwaitingOperatorConfirmation
            or ActivationRequestStatus.WaitingForServiceDesk => "bg-state-pending/15 text-state-pending",
        ActivationRequestStatus.Completed => "bg-state-done/15 text-state-done",
        ActivationRequestStatus.RejectedNoLine
            or ActivationRequestStatus.Closed
            or ActivationRequestStatus.IntegrationFailed
            or ActivationRequestStatus.ActivationFailed => "bg-state-blocked/15 text-state-blocked",
        _ => "bg-state-progress/15 text-state-progress"
    };
}
