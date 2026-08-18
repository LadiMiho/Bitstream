using System.Text.Json;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Integration;

/// <summary>Event types this module acts on for a complaint ticket. Provisional — TRD 11.4 open item 4.</summary>
public static class ComplaintTicketEventTypes
{
    public const string StatusChanged = "STATUS_CHANGED";

    public const string CommentAdded = "COMMENT_ADDED";

    public const string ClosedWithClearingCode = "CLOSED_WITH_CLEARING_CODE";

    /// <summary>TR-PAS-22: CRM completed the ticket without asking for confirmation.</summary>
    public const string AutoCompleted = "AUTO_COMPLETED";

    public const string Reopened = "REOPENED";

    /// <summary>TRD 6.3: the one status that always notifies the ISP, regardless of the configured notifiable-status list.</summary>
    public const string TechnicallyCompletedStatus = "Technically Completed";
}

/// <summary>
/// Implements <see cref="IInboundEventService"/>: TRD 7.3.2 Direction B, for both activation
/// requests (TRD 5) and complaint tickets (TRD 6).
/// <para>
/// The endpoint persists the raw event first, through <see cref="IIntegrationOutbox.RecordInboundAsync"/>
/// (TR-INT-07, TR-INT-24) and returns immediately on a duplicate eventId (TR-INT-25) without
/// calling here again. This class only interprets an already-persisted message: dedup is the
/// outbox's job, ordering and applying the event are this class's. The identifier is looked up
/// as an activation request first and a complaint ticket second — the two series are
/// distinguishable by prefix (TR-DAT-06) but nothing here needs to parse that; whichever lookup
/// finds a row decides the routing.
/// </para>
/// </summary>
public sealed class InboundEventService : IInboundEventService
{
    private readonly IIntegrationOutbox _outbox;
    private readonly IActivationRequestRepository _requestRepository;
    private readonly IActivationRequestService _activationRequestService;
    private readonly IComplaintTicketRepository _ticketRepository;
    private readonly ITicketClosureService _ticketClosureService;
    private readonly INotificationService _notificationService;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOptionsMonitor<CatalogueOptions> _catalogueOptions;
    private readonly ILogger<InboundEventService> _logger;

    public InboundEventService(
        IIntegrationOutbox outbox,
        IActivationRequestRepository requestRepository,
        IActivationRequestService activationRequestService,
        IComplaintTicketRepository ticketRepository,
        ITicketClosureService ticketClosureService,
        INotificationService notificationService,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptionsMonitor<CatalogueOptions> catalogueOptions,
        ILogger<InboundEventService> logger)
    {
        _outbox = outbox;
        _requestRepository = requestRepository;
        _activationRequestService = activationRequestService;
        _ticketRepository = ticketRepository;
        _ticketClosureService = ticketClosureService;
        _notificationService = notificationService;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _catalogueOptions = catalogueOptions;
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

        var activationRequest = await _requestRepository.FindByPublicIdAsync(evt.Identifier, cancellationToken).ConfigureAwait(false);

        if (activationRequest is not null)
        {
            await ApplyToActivationRequestAsync(integrationMessageId, evt, activationRequest, cancellationToken).ConfigureAwait(false);
            return;
        }

        var ticket = await _ticketRepository.FindByPublicIdAsync(evt.Identifier, cancellationToken).ConfigureAwait(false);

        if (ticket is not null)
        {
            await ApplyToComplaintTicketAsync(integrationMessageId, evt, ticket, cancellationToken).ConfigureAwait(false);
            return;
        }

        throw new InboundEventNotFoundException($"No activation request or complaint ticket found for identifier '{evt.Identifier}'.");
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
                or ActivationRequestValidationException or ActivationRequestConflictException or ActivationRequestNotFoundException
                or ComplaintTicketValidationException or TicketClosureConflictException or TicketClosureNotFoundException
                or TicketClosureValidationException)
            {
                // TR-INT-31: one bad message must not abort the rest of the replay window; the
                // message is left exactly as it was for another look, and the reason is logged.
                _logger.LogWarning(exception, "Replay of message {MessageId} did not apply.", message.MessageId);
            }
        }
    }

    private async Task ApplyToActivationRequestAsync(
        long integrationMessageId, InboundTicketEvent evt, ActivationRequest request, CancellationToken cancellationToken)
    {
        // TR-INT-25 / TR-PAS-17: an event no later than the last one already applied is
        // discarded, not applied — and this is not an error, so the message is still marked
        // succeeded rather than left for a retry that would only reach the same conclusion.
        if (request.LastAppliedEventAt is { } lastApplied && evt.OccurredAt <= lastApplied)
        {
            await DiscardStaleAsync(integrationMessageId, evt, lastApplied, cancellationToken).ConfigureAwait(false);
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
                // TR-INT-27: an event type this module does not act on for an activation request
                // is surfaced to the administrator rather than silently accepted — the
                // complaint-ticket event types fall here too when they target an activation
                // request identifier, which is never valid.
                throw new InboundEventNotApplicableException(
                    $"Event type '{evt.EventType}' does not apply to activation request '{evt.Identifier}'.");
        }

        request.LastAppliedEventAt = evt.OccurredAt;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await MarkAppliedAsync(integrationMessageId, evt, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyToComplaintTicketAsync(
        long integrationMessageId, InboundTicketEvent evt, ComplaintTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.LastAppliedEventAt is { } lastApplied && evt.OccurredAt <= lastApplied)
        {
            await DiscardStaleAsync(integrationMessageId, evt, lastApplied, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (evt.EventType)
        {
            case ComplaintTicketEventTypes.StatusChanged:
                if (string.IsNullOrWhiteSpace(evt.Payload.Status))
                {
                    throw new ComplaintTicketValidationException("payload.status is required for a STATUS_CHANGED event.");
                }

                await ApplyStatusChangeAsync(ticket, evt.Payload, cancellationToken).ConfigureAwait(false);
                break;

            case ComplaintTicketEventTypes.CommentAdded:
                if (string.IsNullOrWhiteSpace(evt.Payload.Comment))
                {
                    throw new ComplaintTicketValidationException("payload.comment is required for a COMMENT_ADDED event.");
                }

                await AppendCrmCommentAsync(ticket, evt.Payload, cancellationToken).ConfigureAwait(false);
                break;

            case ComplaintTicketEventTypes.ClosedWithClearingCode:
                if (string.IsNullOrWhiteSpace(evt.Payload.ClearingCode))
                {
                    throw new ComplaintTicketValidationException("payload.clearingCode is required for a CLOSED_WITH_CLEARING_CODE event.");
                }

                // TR-PAS-18: applies through the same service the manual path (if there ever is
                // one) would use, so the Pending ISP Confirmation window is started exactly once,
                // the same way regardless of caller.
                await _ticketClosureService.ApplyClearingCodeAsync(evt.Identifier, evt.Payload.ClearingCode, evt.Payload.ClearingText, cancellationToken)
                    .ConfigureAwait(false);
                break;

            case ComplaintTicketEventTypes.AutoCompleted:
                ApplyAutoCompleted(ticket, _clock.UtcNow);
                break;

            case ComplaintTicketEventTypes.Reopened:
                ApplyReopened(ticket);
                break;

            default:
                throw new InboundEventNotApplicableException(
                    $"Event type '{evt.EventType}' does not apply to complaint ticket '{evt.Identifier}'.");
        }

        // TicketClosureService.ApplyClearingCodeAsync (above) reads and saves the same tracked
        // entity through its own repository call, sharing this scope's DbContext — so `ticket`
        // already reflects whatever it changed, and one more save here is enough regardless of
        // which branch ran.
        ticket.LastAppliedEventAt = evt.OccurredAt;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await MarkAppliedAsync(integrationMessageId, evt, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyStatusChangeAsync(ComplaintTicket ticket, InboundTicketEventPayload payload, CancellationToken cancellationToken)
    {
        ticket.Status = payload.Status!;

        // TR-PAS-13/16/17: an internal forward (a routing group set) is recorded but never
        // notified. Technically Completed always notifies — TRD 6.3 names it explicitly, so it
        // does not wait on the configurable notifiable-status list (TRD 11.4 open item 4)
        // the way every other status does.
        var isInternalForward = !string.IsNullOrWhiteSpace(payload.ForwardingGroup);
        var alwaysNotifies = string.Equals(payload.Status, ComplaintTicketEventTypes.TechnicallyCompletedStatus, StringComparison.OrdinalIgnoreCase);
        var configuredNotifiable = _catalogueOptions.CurrentValue.IspNotifiableStatuses.Contains(payload.Status, StringComparer.OrdinalIgnoreCase);

        if (isInternalForward || (!alwaysNotifies && !configuredNotifiable))
        {
            return;
        }

        await _notificationService.QueueAsync(
            "TICKET_STATUS_NOTIFICATION",
            new Dictionary<string, string> { ["ticketPublicId"] = ticket.PublicId, ["status"] = payload.Status! },
            "ComplaintTicket", ticket.TicketId, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendCrmCommentAsync(ComplaintTicket ticket, InboundTicketEventPayload payload, CancellationToken cancellationToken)
    {
        var comment = new TicketComment
        {
            TicketId = ticket.TicketId,
            Ticket = ticket,
            AuthorType = CommentAuthorType.Crm,
            AuthorDisplayName = string.IsNullOrWhiteSpace(payload.Agent) ? "CRM" : payload.Agent,
            Body = payload.Comment!,
            CreatedAt = _clock.UtcNow,
            // Originated in CRM: there is nothing to replicate back out (TR-PAS-26).
            CrmSyncStatus = "NotApplicable"
        };

        await _ticketRepository.AddCommentAsync(comment, cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyAutoCompleted(ComplaintTicket ticket, DateTimeOffset now)
    {
        // TR-PAS-22: no confirmation window — CRM has already completed the ticket.
        ticket.ClosureDecision = ClosureDecision.CompletedByCrm;
        ticket.ClosureDecisionAt = now;
        ticket.Status = "Closed";
        ticket.ClosedAt = now;
        ticket.ConfirmationDueAt = null;
    }

    private static void ApplyReopened(ComplaintTicket ticket)
    {
        ticket.Status = "Reopened";
        ticket.ClosureDecision = null;
        ticket.ClosureDecisionAt = null;
        ticket.ClosureDecisionBy = null;
        ticket.ConfirmationDueAt = null;
        ticket.ClearingCode = null;
        ticket.ClearingText = null;
        ticket.ClearingCodeAppliedAt = null;
        ticket.ClosedAt = null;
    }

    private async Task DiscardStaleAsync(long integrationMessageId, InboundTicketEvent evt, DateTimeOffset lastApplied, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "Discarding stale event {EventId} for {Identifier}: occurredAt {OccurredAt:O} is not after the last applied event {LastApplied:O}.",
            evt.EventId, evt.Identifier, evt.OccurredAt, lastApplied);

        await _outbox.MarkSucceededAsync(
            integrationMessageId,
            "{\"discarded\":true,\"reason\":\"stale: occurredAt is not after the last applied event\"}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkAppliedAsync(long integrationMessageId, InboundTicketEvent evt, CancellationToken cancellationToken)
    {
        await _outbox.MarkSucceededAsync(
            integrationMessageId, $"{{\"applied\":true,\"eventType\":{JsonSerializer.Serialize(evt.EventType)}}}", cancellationToken)
            .ConfigureAwait(false);
    }
}
