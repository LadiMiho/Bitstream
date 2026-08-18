using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Transactional outbox and inbox (TR-ARC-03, TR-INT-07, TR-INT-24).
/// Application services enqueue here inside the same transaction as the business write;
/// a background dispatcher, not the request thread, calls the gateways.
/// </summary>
public interface IIntegrationOutbox
{
    /// <summary>
    /// Persists an outbound message. Must participate in the caller's transaction so that
    /// a business write and its message commit together or not at all.
    /// </summary>
    Task<long> EnqueueOutboundAsync(
        TargetSystem targetSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string payload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an inbound event in raw form before interpretation (TR-INT-24) and returns
    /// the existing record when <paramref name="idempotencyKey"/> was already accepted, so
    /// that a repeated eventId returns the original outcome (TR-INT-25).
    /// </summary>
    Task<(IntegrationMessage Message, bool IsDuplicate)> RecordInboundAsync(
        TargetSystem sourceSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string rawPayload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default);

    /// <summary>Claims a batch of due messages for dispatch.</summary>
    Task<IReadOnlyList<IntegrationMessage>> ClaimDueOutboundAsync(
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up one message by ID, for interpreting a just-recorded inbound event or inspecting a dead letter.</summary>
    Task<IntegrationMessage?> FindByIdAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>Records a successful dispatch and the stored response.</summary>
    Task MarkSucceededAsync(
        long messageId,
        string? responsePayload,
        CancellationToken cancellationToken = default);

    /// <summary>Records a failure and schedules the next attempt, or dead-letters it (TR-INT-04).</summary>
    Task MarkFailedAsync(
        long messageId,
        string error,
        bool retryable,
        CancellationToken cancellationToken = default);

    /// <summary>Lists dead-lettered messages for administrator inspection (TR-INT-05).</summary>
    Task<IReadOnlyList<IntegrationMessage>> GetDeadLetteredAsync(
        TargetSystem? targetSystem,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Re-queues a dead-lettered message without duplicating its effect (TR-INT-05).</summary>
    Task ReplayAsync(long messageId, CancellationToken cancellationToken = default);

    /// <summary>Finds inbound messages for recovery replay (TR-INT-31): by identifier, by time window, or both.</summary>
    Task<IReadOnlyList<IntegrationMessage>> FindInboundAsync(
        string? relatedPublicId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default);
}
