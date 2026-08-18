using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Implements <see cref="IIntegrationOutbox"/> over <see cref="BitstreamDbContext"/>
/// (TR-ARC-03, TR-INT-04, TR-INT-24, TR-INT-25).
/// <para>
/// Storage only: this class enqueues, claims, marks and replays rows, but never calls a
/// gateway itself — <c>OutboxDispatcher</c> (Application layer) does that, so that persistence
/// stays free of any reference to <c>ICrmGateway</c> or the other integration ports.
/// </para>
/// </summary>
public sealed class IntegrationOutbox : IIntegrationOutbox
{
    private readonly BitstreamDbContext _dbContext;
    private readonly IClock _clock;

    public IntegrationOutbox(BitstreamDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<long> EnqueueOutboundAsync(
        TargetSystem targetSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string payload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var message = new IntegrationMessage
        {
            Direction = IntegrationDirection.Outbound,
            TargetSystem = targetSystem,
            InterfaceCode = interfaceCode,
            MessageType = messageType,
            Payload = payload,
            IdempotencyKey = idempotencyKey,
            Status = IntegrationMessageStatus.Pending,
            RelatedPublicId = relatedPublicId,
            CorrelationId = correlationId,
            CreatedAt = _clock.UtcNow
        };

        await _dbContext.IntegrationMessages.AddAsync(message, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return message.MessageId;
    }

    public async Task<(IntegrationMessage Message, bool IsDuplicate)> RecordInboundAsync(
        TargetSystem sourceSystem,
        string interfaceCode,
        string messageType,
        string idempotencyKey,
        string rawPayload,
        string correlationId,
        string? relatedPublicId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // TR-INT-25: a repeated eventId returns the original record rather than inserting again.
        var existing = await _dbContext.IntegrationMessages.FirstOrDefaultAsync(
            m => m.Direction == IntegrationDirection.Inbound &&
                 m.TargetSystem == sourceSystem &&
                 m.InterfaceCode == interfaceCode &&
                 m.IdempotencyKey == idempotencyKey,
            cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return (existing, true);
        }

        var message = new IntegrationMessage
        {
            Direction = IntegrationDirection.Inbound,
            TargetSystem = sourceSystem,
            InterfaceCode = interfaceCode,
            MessageType = messageType,
            Payload = rawPayload,
            IdempotencyKey = idempotencyKey,
            Status = IntegrationMessageStatus.Pending,
            RelatedPublicId = relatedPublicId,
            CorrelationId = correlationId,
            CreatedAt = _clock.UtcNow
        };

        await _dbContext.IntegrationMessages.AddAsync(message, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return (message, false);
    }

    public async Task<IReadOnlyList<IntegrationMessage>> ClaimDueOutboundAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        var due = await _dbContext.IntegrationMessages
            .Where(m => m.Direction == IntegrationDirection.Outbound &&
                        (m.Status == IntegrationMessageStatus.Pending ||
                         (m.Status == IntegrationMessageStatus.Failed && m.NextRetryAt != null && m.NextRetryAt <= now)))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in due)
        {
            message.Status = IntegrationMessageStatus.InFlight;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return due;
    }

    public Task<IntegrationMessage?> FindByIdAsync(long messageId, CancellationToken cancellationToken = default) =>
        _dbContext.IntegrationMessages.FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken);

    public async Task MarkSucceededAsync(long messageId, string? responsePayload, CancellationToken cancellationToken = default)
    {
        var message = await FindOrThrowAsync(messageId, cancellationToken).ConfigureAwait(false);

        message.Status = IntegrationMessageStatus.Succeeded;
        message.ResponsePayload = responsePayload;
        message.ProcessedAt = _clock.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(long messageId, string error, bool retryable, CancellationToken cancellationToken = default)
    {
        var message = await FindOrThrowAsync(messageId, cancellationToken).ConfigureAwait(false);

        message.Attempts++;
        message.LastError = error;

        if (retryable)
        {
            // TR-INT-04: exponential backoff, capped at 30 minutes between attempts.
            var backoffMinutes = Math.Min(30, Math.Pow(2, Math.Min(message.Attempts, 6)));
            message.Status = IntegrationMessageStatus.Failed;
            message.NextRetryAt = _clock.UtcNow.AddMinutes(backoffMinutes);
        }
        else
        {
            // Retry budget exhausted, or a non-retryable business rejection (TR-INT-19).
            message.Status = IntegrationMessageStatus.DeadLettered;
            message.NextRetryAt = null;
            message.ProcessedAt = _clock.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntegrationMessage>> GetDeadLetteredAsync(
        TargetSystem? targetSystem,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.IntegrationMessages
            .Where(m => m.Status == IntegrationMessageStatus.DeadLettered);

        if (targetSystem is { } target)
        {
            query = query.Where(m => m.TargetSystem == target);
        }

        return await query
            .OrderBy(m => m.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReplayAsync(long messageId, CancellationToken cancellationToken = default)
    {
        var message = await FindOrThrowAsync(messageId, cancellationToken).ConfigureAwait(false);

        // Re-queued without duplicating its effect (TR-INT-05): the idempotency key is unchanged,
        // so the receiver still recognises it as the same message if it was in fact delivered.
        message.Status = IntegrationMessageStatus.Pending;
        message.NextRetryAt = null;
        message.LastError = null;

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IntegrationMessage>> FindInboundAsync(
        string? relatedPublicId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.IntegrationMessages.Where(m => m.Direction == IntegrationDirection.Inbound);

        if (!string.IsNullOrWhiteSpace(relatedPublicId))
        {
            query = query.Where(m => m.RelatedPublicId == relatedPublicId);
        }

        if (fromUtc is { } from)
        {
            query = query.Where(m => m.CreatedAt >= from);
        }

        if (toUtc is { } to)
        {
            query = query.Where(m => m.CreatedAt <= to);
        }

        return await query.OrderBy(m => m.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IntegrationMessage> FindOrThrowAsync(long messageId, CancellationToken cancellationToken)
    {
        var message = await _dbContext.IntegrationMessages.FirstOrDefaultAsync(m => m.MessageId == messageId, cancellationToken).ConfigureAwait(false);

        return message ?? throw new InvalidOperationException($"Integration message {messageId} does not exist.");
    }
}
