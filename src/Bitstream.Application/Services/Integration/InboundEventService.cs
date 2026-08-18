using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Bitstream.Application.Services.Integration;

/// <summary>
/// Implements <see cref="IInboundEventService"/>: TRD 7.3.2 Direction B.
/// <para>
/// The endpoint persists the raw event first, through <see cref="IIntegrationOutbox.RecordInboundAsync"/>
/// (TR-INT-07, TR-INT-24) and returns immediately on a duplicate eventId (TR-INT-25) without
/// calling here again. This class only interprets an already-persisted message: dedup is the
/// outbox's job, ordering and applying the event are this class's.
/// </para>
/// </summary>
public sealed class InboundEventService : IInboundEventService
{
    private readonly IIntegrationOutbox _outbox;
    private readonly IActivationRequestRepository _requestRepository;
    private readonly IActivationRequestService _activationRequestService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<InboundEventService> _logger;

    public InboundEventService(
        IIntegrationOutbox outbox,
        IActivationRequestRepository requestRepository,
        IActivationRequestService activationRequestService,
        IUnitOfWork unitOfWork,
        ILogger<InboundEventService> logger)
    {
        _outbox = outbox;
        _requestRepository = requestRepository;
        _activationRequestService = activationRequestService;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task ApplyAsync(long integrationMessageId, CancellationToken cancellationToken = default)
    {
        var message = await _outbox.FindByIdAsync(integrationMessageId, cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException($"Integration message {integrationMessageId} does not exist.");

        if (message.Status == IntegrationMessageStatus.Succeeded)
        {
            // A replay of an already-applied message is a safe no-op (TR-INT-31); the endpoint's
            // own duplicate-eventId short-circuit is what stops this from happening on a normal
            // repeated delivery.
            return;
        }

        var evt = JsonSerializer.Deserialize<InboundTicketEvent>(message.Payload) ??
            throw new InvalidOperationException($"Integration message {integrationMessageId} payload could not be deserialised.");

        var request = await _requestRepository.FindByPublicIdAsync(evt.Identifier, cancellationToken).ConfigureAwait(false) ??
            throw new InboundEventNotFoundException($"No activation request found for identifier '{evt.Identifier}'.");

        // TR-INT-25 / TR-PAS-17: an event no later than the last one already applied is
        // discarded, not applied — and this is not an error, so the message is still marked
        // succeeded rather than left for a retry that would only reach the same conclusion.
        if (request.LastAppliedEventAt is { } lastApplied && evt.OccurredAt <= lastApplied)
        {
            _logger.LogWarning(
                "Discarding stale event {EventId} for {Identifier}: occurredAt {OccurredAt:O} is not after the last applied event {LastApplied:O}.",
                evt.EventId, evt.Identifier, evt.OccurredAt, lastApplied);

            await _outbox.MarkSucceededAsync(
                integrationMessageId,
                "{\"discarded\":true,\"reason\":\"stale: occurredAt is not after the last applied event\"}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (evt.EventType)
        {
            case ActivationEventTypes.SalesOrderOpened:
                if (string.IsNullOrWhiteSpace(evt.Payload.SalesOrderId))
                {
                    throw new ActivationRequestValidationException("payload.salesOrderId is required for a SALES_ORDER_OPENED event.");
                }

                await _activationRequestService.ApplySalesOrderAsync(evt.Identifier, evt.Payload.SalesOrderId, cancellationToken).ConfigureAwait(false);
                break;

            case ActivationEventTypes.ProvisioningStarted:
                await _activationRequestService.StartProvisioningAsync(evt.Identifier, cancellationToken).ConfigureAwait(false);
                break;

            case ActivationEventTypes.TechnicallyCompleted:
                await _activationRequestService.CompleteAsync(evt.Identifier, cancellationToken).ConfigureAwait(false);
                break;

            default:
                // TR-INT-27: an event type this module does not (yet) act on is surfaced to the
                // administrator rather than silently accepted — complaint-ticket lifecycle events
                // (STATUS_CHANGED, COMMENT_ADDED, CLOSED_WITH_CLEARING_CODE, AUTO_COMPLETED,
                // REOPENED) fall here too, since that module is not built yet.
                throw new InboundEventNotApplicableException(
                    $"Event type '{evt.EventType}' does not apply to activation request '{evt.Identifier}' " +
                    "(TRD 11.4 open item 4 leaves the full event vocabulary open).");
        }

        request.LastAppliedEventAt = evt.OccurredAt;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _outbox.MarkSucceededAsync(
            integrationMessageId, $"{{\"applied\":true,\"eventType\":{JsonSerializer.Serialize(evt.EventType)}}}", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ReplayAsync(string? ticketPublicId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken = default)
    {
        var candidates = await _outbox.FindInboundAsync(ticketPublicId, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);

        foreach (var message in candidates)
        {
            try
            {
                await ApplyAsync(message.MessageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InboundEventNotFoundException or InboundEventNotApplicableException
                or ActivationRequestValidationException or ActivationRequestConflictException or ActivationRequestNotFoundException)
            {
                // TR-INT-31: one bad message must not abort the rest of the replay window; the
                // message is left exactly as it was for another look, and the reason is logged.
                _logger.LogWarning(exception, "Replay of message {MessageId} did not apply.", message.MessageId);
            }
        }
    }
}
