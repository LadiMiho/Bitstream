using Bitstream.Domain.Entities;

namespace Bitstream.Web.Presentation;

/// <summary>Display labels and pill colours for <see cref="PostActivationRequestStatus"/>.</summary>
public static class PostActivationRequestLabels
{
    public static string For(string status) => status switch
    {
        PostActivationRequestStatus.PendingCrmSync => "Pending CRM sync",
        PostActivationRequestStatus.InProcess => "In process",
        PostActivationRequestStatus.TechnicallyCompleted => "Technically completed",
        PostActivationRequestStatus.Submitted => "Submitted to CRM",
        PostActivationRequestStatus.AlreadyExists => "Already exists in CRM",
        PostActivationRequestStatus.Failed => "Failed",
        _ => status
    };

    public static string PillClass(string status) => "status-pill " + status switch
    {
        PostActivationRequestStatus.PendingCrmSync => "bg-state-pending/15 text-state-pending",
        PostActivationRequestStatus.InProcess => "bg-state-progress/15 text-state-progress",
        PostActivationRequestStatus.TechnicallyCompleted => "bg-state-done/15 text-state-done",
        PostActivationRequestStatus.Submitted => "bg-state-progress/15 text-state-progress",
        PostActivationRequestStatus.AlreadyExists => "bg-state-progress/15 text-state-progress",
        _ => "bg-state-blocked/15 text-state-blocked"
    };
}
