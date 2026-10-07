namespace Bitstream.Application.Services.Integration;

/// <summary>
/// Application-layer shape of an inbound CRM event (TRD 7.3.2). Deliberately independent of
/// <c>Bitstream.Api.Contracts.TicketEventRequest</c> — the Api project's wire contract — because
/// the application layer must not reference the Api project (TR-ARC-01). The endpoint maps one
/// to the other; the JSON property names are the same on both, by convention, so the mapping is
/// a straight field-for-field copy.
/// </summary>
public sealed record InboundTicketEvent(
    string EventId,
    string EventType,
    string Identifier,
    string? CrmTicketId,
    DateTimeOffset OccurredAt,
    InboundTicketEventPayload Payload);

/// <summary>
/// Body of an inbound CRM event. One shape carries every event type this interface accepts
/// (TR-INT-22) — fields not used by a given <see cref="InboundTicketEvent.EventType"/> are null.
/// </summary>
public sealed record InboundTicketEventPayload(
    string? Status,
    string? Comment,
    string? ClearingCode,
    string? ClearingText,
    string? ClosedBy,
    bool? RequiresIspConfirmation,
    string? ForwardingGroup,
    string? Agent,
    string? SalesOrderId,
    string? BusinessPartner,
    string? Reason = null);

/// <summary>Recognised event types for an activation request (TRD 5.3). Provisional: the complete vocabulary is TRD 11.4 open item 4.</summary>
public static class ActivationEventTypes
{
    /// <summary>GIS line check done in CRM: a line is available (AwaitingGisVerification → LineAvailable).</summary>
    public const string LineAvailable = "LINE_AVAILABLE";

    /// <summary>GIS line check done in CRM: no line (AwaitingGisVerification → RejectedNoLine). Requires <see cref="InboundTicketEventPayload.Reason"/>.</summary>
    public const string NoLine = "NO_LINE";

    /// <summary>INT-CRM-03. Requires <see cref="InboundTicketEventPayload.SalesOrderId"/>.</summary>
    public const string SalesOrderOpened = "SALES_ORDER_OPENED";

    /// <summary>CRM has activated the line (SalesOrderOpened → AwaitingOperatorConfirmation); the ISP's operator must now confirm it works.</summary>
    public const string LineActivated = "LINE_ACTIVATED";
}

/// <summary>Thrown when an event's identifier resolves to no known request or ticket. Maps to 404.</summary>
public sealed class InboundEventNotFoundException : Exception
{
    public InboundEventNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>Thrown when an event's shape is recognised but its type does not apply to the entity it targets. Maps to 422 (TR-INT-27).</summary>
public sealed class InboundEventNotApplicableException : Exception
{
    public InboundEventNotApplicableException(string message)
        : base(message)
    {
    }
}
