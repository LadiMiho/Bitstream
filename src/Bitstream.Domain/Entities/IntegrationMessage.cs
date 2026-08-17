using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Outbox and inbox record for every integration message. TRD 3.1 "IntegrationMessage".
/// Outbound messages are persisted before dispatch (TR-ARC-03); inbound events are
/// persisted in raw form before interpretation so they can be replayed (TR-INT-24).
/// </summary>
public sealed class IntegrationMessage
{
    public long MessageId { get; set; }

    public IntegrationDirection Direction { get; set; }

    public TargetSystem TargetSystem { get; set; }

    /// <summary>Interface code from TRD 7.1, e.g. INT-CRM-02.</summary>
    public required string InterfaceCode { get; set; }

    /// <summary>Event or operation type, e.g. CLOSED_WITH_CLEARING_CODE.</summary>
    public string? MessageType { get; set; }

    /// <summary>Raw payload as sent or as received, before any mapping (TR-INT-24).</summary>
    public required string Payload { get; set; }

    /// <summary>
    /// Deduplication key: the public request/ticket identifier outbound (TR-INT-17),
    /// the CRM <c>eventId</c> inbound (TR-INT-25). Unique per direction and target system.
    /// </summary>
    public required string IdempotencyKey { get; set; }

    public IntegrationMessageStatus Status { get; set; } = IntegrationMessageStatus.Pending;

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>Exponential backoff schedule (TR-INT-04).</summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>Public identifier of the related request or ticket, for reconciliation (TR-INT-02).</summary>
    public string? RelatedPublicId { get; set; }

    public required string CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Stored response, so a repeated eventId returns the original result (TR-INT-25).</summary>
    public string? ResponsePayload { get; set; }
}
