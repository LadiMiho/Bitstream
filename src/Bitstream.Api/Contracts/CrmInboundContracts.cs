using System.Text.Json.Serialization;

namespace Bitstream.Api.Contracts;

/// <summary>
/// Inbound event sent by CRM, TRD 7.3.2. One payload carries every CRM-originated lifecycle
/// update: status changes, comments, the clearing code at closure and automatic completion.
/// </summary>
/// <param name="EventId">Deduplication key. A repeated eventId returns the original result and re-applies nothing (TR-INT-25).</param>
/// <param name="EventType">
/// Activation requests currently act on SALES_ORDER_OPENED, PROVISIONING_STARTED and
/// TECHNICALLY_COMPLETED (TRD 5.3). STATUS_CHANGED, COMMENT_ADDED, CLOSED_WITH_CLEARING_CODE,
/// AUTO_COMPLETED and REOPENED are complaint-ticket lifecycle events (TRD 6) recognised as valid
/// shape but not yet acted on, since that module is not built. Any of these, or an entirely
/// unknown type, is rejected with 422 (TR-INT-27) — the vocabulary itself is TRD 11.4 open item 4.
/// </param>
/// <param name="Identifier">Portal public identifier of the ticket, e.g. ISP_1024.</param>
/// <param name="CrmTicketId">CRM-side identifier, accepted as an alternative lookup key if agreed.</param>
/// <param name="OccurredAt">Event time in UTC. Determines order per ticket; an event older than the last applied one is discarded (TR-INT-25).</param>
/// <param name="Payload">Event body.</param>
public sealed record TicketEventRequest(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("crmTicketId")] string? CrmTicketId,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("payload")] TicketEventPayload Payload);

/// <summary>
/// Body of an inbound CRM event.
/// <para>
/// The complete list of CRM statuses and event types is TRD 11.4 open item 4, so no
/// enumeration is fixed here: values are carried as strings, validated against the
/// configured vocabulary at processing time, and an unrecognised value is surfaced to the
/// administrator rather than guessed at (TR-INT-27).
/// </para>
/// </summary>
/// <param name="Status">CRM status code after the event.</param>
/// <param name="Comment">Comment text, for COMMENT_ADDED (TR-PAS-26).</param>
/// <param name="ClearingCode">Resolution code, for CLOSED_WITH_CLEARING_CODE (TR-PAS-18).</param>
/// <param name="ClearingText">Free-text resolution description.</param>
/// <param name="ClosedBy">Party that closed the ticket, e.g. FM Contractor.</param>
/// <param name="RequiresIspConfirmation">True when the ISP must Confirm or reject (TRD 6.4); false for automatic completion (TR-PAS-22).</param>
/// <param name="ForwardingGroup">Internal group the ticket was forwarded to. Recorded but never notified to the ISP (TR-PAS-13, TR-PAS-14).</param>
/// <param name="Agent">CRM agent who raised the event.</param>
/// <param name="SalesOrderId">For SALES_ORDER_OPENED (INT-CRM-03, TR-ACT-18): the sales order reference to store against the activation request.</param>
/// <param name="BusinessPartner">For SALES_ORDER_OPENED: the customer BP the sales order was raised for (TR-ACT-15).</param>
public sealed record TicketEventPayload(
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("clearingCode")] string? ClearingCode,
    [property: JsonPropertyName("clearingText")] string? ClearingText,
    [property: JsonPropertyName("closedBy")] string? ClosedBy,
    [property: JsonPropertyName("requiresIspConfirmation")] bool? RequiresIspConfirmation,
    [property: JsonPropertyName("forwardingGroup")] string? ForwardingGroup,
    [property: JsonPropertyName("agent")] string? Agent,
    [property: JsonPropertyName("salesOrderId")] string? SalesOrderId,
    [property: JsonPropertyName("businessPartner")] string? BusinessPartner);

/// <summary>
/// Acknowledgement returned to CRM. Sent only after the event has been persisted
/// (TR-INT-07); the interpretation runs afterwards so that CRM is never held open on
/// portal-side work (TRD 7.3.2, TR-INT-30).
/// </summary>
/// <param name="EventId">Echo of the accepted event.</param>
/// <param name="Identifier">Portal identifier the event was matched to.</param>
/// <param name="Duplicate">True when this eventId had already been accepted. Not an error.</param>
/// <param name="ReceivedAt">Time the portal persisted the event.</param>
public sealed record TicketEventAccepted(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("identifier")] string Identifier,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt);

/// <summary>Request to reprocess events for a ticket or a time window during recovery (TR-INT-31).</summary>
/// <param name="TicketIdentifier">Portal identifier to replay; null replays the whole window.</param>
/// <param name="FromUtc">Start of the window.</param>
/// <param name="ToUtc">End of the window.</param>
public sealed record EventReplayRequest(
    [property: JsonPropertyName("ticketIdentifier")] string? TicketIdentifier,
    [property: JsonPropertyName("fromUtc")] DateTimeOffset? FromUtc,
    [property: JsonPropertyName("toUtc")] DateTimeOffset? ToUtc);
