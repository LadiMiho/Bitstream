using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Complaint ticket raised in the portal and mirrored in CRM. TRD 3.1 "ComplaintTicket", TRD 6.2–6.6.
/// CRM remains the master of ticket state; this record is the ISP-visible projection.
/// </summary>
public sealed class ComplaintTicket
{
    public long TicketId { get; set; }

    /// <summary>Separate identifier series from activation requests (TR-DAT-06).</summary>
    public required string PublicId { get; set; }

    public long IspId { get; set; }

    public Isp Isp { get; set; } = null!;

    public long LineId { get; set; }

    public ActiveLine Line { get; set; } = null!;

    /// <summary>Three-level cascading defect category aligned to the CRM catalogue (TR-PAS-08).</summary>
    public required string CategoryL1 { get; set; }

    public required string CategoryL2 { get; set; }

    public required string CategoryL3 { get; set; }

    /// <summary>Mandatory, max 4000 characters, HTML stripped (TRD 6.2).</summary>
    public required string Description { get; set; }

    /// <summary>
    /// Status code. Held as a code rather than an enum because the CRM status list is
    /// configurable and not yet agreed (TR-PAS-16, TRD 11.4 open item 4).
    /// </summary>
    public required string Status { get; set; }

    public string? CrmTicketId { get; set; }

    /// <summary>Resolution code transmitted by CRM at closure, shown to the ISP (TR-PAS-18).</summary>
    public string? ClearingCode { get; set; }

    public string? ClearingText { get; set; }

    /// <summary>Confirm / No / auto-confirmation outcome (TR-PAS-19, TR-PAS-21c).</summary>
    public ClosureDecision? ClosureDecision { get; set; }

    public DateTimeOffset? ClosureDecisionAt { get; set; }

    public long? ClosureDecisionBy { get; set; }

    /// <summary>Deadline of the Pending ISP Confirmation window, in working days (TR-PAS-21a/h).</summary>
    public DateTimeOffset? ConfirmationDueAt { get; set; }

    /// <summary>Original ticket when this is a post-closure challenge follow-up (TR-PAS-21f).</summary>
    public long? ParentTicketId { get; set; }

    public ComplaintTicket? ParentTicket { get; set; }

    /// <summary><c>occurredAt</c> of the last applied inbound CRM event; enforces ordering (TR-INT-25).</summary>
    public DateTimeOffset? LastAppliedEventAt { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public long? OpenedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public ICollection<TicketComment> Comments { get; set; } = [];
}
