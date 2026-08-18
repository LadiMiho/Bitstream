using System.Text.Json;
using Bitstream.Api.Contracts;
using Bitstream.Application.Abstractions;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.Integration;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// Direction B of the CRM integration, TRD 7.3.2: the single versioned inbound API through
/// which CRM transmits every ticket lifecycle update (TR-INT-22).
/// <para>
/// This covers TRD 7.1 rows INT-CRM-03 (sales order notification), INT-CRM-05 (ticket
/// lifecycle events), INT-CRM-07 (closure and clearing code) and the inbound half of
/// INT-CRM-06 (comment replication) — one endpoint, distinguished by event type, exactly as
/// TR-INT-22 requires. Only the activation request events (SALES_ORDER_OPENED,
/// PROVISIONING_STARTED, TECHNICALLY_COMPLETED) are actually acted on; the complaint-ticket
/// events are recognised as valid shape but rejected with 422 because that module is not built.
/// </para>
/// <para>
/// Authentication is not yet configured: the method is TRD 11.4 open item 3.
/// </para>
/// </summary>
public static class CrmInboundEndpoints
{
    /// <summary>Maps the CRM-facing inbound interface under /api/v1.</summary>
    public static IEndpointRouteBuilder MapCrmInboundEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // TR-INT-29: the version is in the path; a breaking change becomes /api/v2 with v1
        // supported for the agreed transition period.
        var group = app.MapGroup("/api/v1/tickets")
            .WithTags("CRM inbound (INT-CRM-03, -05, -06, -07)")
            .RequireRateLimiting(RateLimitPolicies.CrmInbound);

        group.MapPost("/{identifier}/events", SubmitEvent)
            .WithName("SubmitTicketEvent")
            .WithSummary("Accept a CRM-originated ticket lifecycle event")
            .WithDescription(
                """
                Single inbound interface for every CRM-originated update (TR-INT-22).

                Response codes:
                  200 — accepted: newly applied, applied-but-discarded-as-stale, or a duplicate
                        eventId (Duplicate=true in the body; nothing re-applied, TR-INT-25).
                  400 — malformed request, or the route identifier does not match the body's.
                  404 — identifier does not resolve to any known request.
                  409 — event type is a valid TRD 5.3 concept but not a permitted transition from
                        the request's current status.
                  422 — event type is recognised shape but not applicable to this identifier
                        (TR-INT-27) — including every complaint-ticket event, since that module is
                        not built yet — or the payload is missing a field the event type requires.
                  429 — rate limited (TR-SEC-29, TR-INT-30).

                Behaviour:
                  * The raw payload is persisted before interpretation (TR-INT-24), so a mapping
                    defect can be corrected and events replayed without asking CRM to resend.
                  * eventId is the deduplication key (TR-INT-25).
                  * occurredAt orders events per ticket; an event no later than the last one
                    applied is discarded, not applied (TR-INT-25, TR-PAS-17).

                Open items: the authentication method and CRM source IP ranges (TRD 11.4 open
                item 3), and the complete status and event type list (open item 4).
                """)
            .Accepts<TicketEventRequest>("application/json")
            .Produces<TicketEventAccepted>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/events/replay", ReplayEvents)
            .WithName("ReplayTicketEvents")
            .WithSummary("Reprocess persisted CRM events for a ticket or time window")
            .WithDescription(
                """
                Administrative recovery function (TR-INT-31). Reprocesses events already persisted
                by this interface; it does not ask CRM to resend. Idempotent: an event already
                applied is a no-op, and one event that still cannot apply does not abort the rest
                of the window — it is logged and left for another look.
                """)
            .Accepts<EventReplayRequest>("application/json")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    /// <summary>POST /api/v1/tickets/{identifier}/events — TRD 7.3.2.</summary>
    /// <param name="identifier">Portal public identifier of the ticket, e.g. ISP_1024.</param>
    /// <param name="request">The CRM event.</param>
    /// <param name="outbox">Records the raw inbound message before interpretation (TR-INT-07).</param>
    /// <param name="inboundEventService">Applies the event to the matching activation request or complaint ticket.</param>
    /// <param name="correlationContext">Current request's correlation ID (TR-ARC-04).</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    private static async Task<IResult> SubmitEvent(
        [FromRoute] string identifier,
        [FromBody] TicketEventRequest request,
        IIntegrationOutbox outbox,
        IInboundEventService inboundEventService,
        ICorrelationContext correlationContext,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Identifier, identifier, StringComparison.Ordinal))
        {
            return Results.Problem(
                title: "Identifier mismatch",
                detail: $"Route identifier '{identifier}' does not match the body's identifier '{request.Identifier}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.EventId) || string.IsNullOrWhiteSpace(request.EventType))
        {
            return Results.Problem(
                title: "Malformed event",
                detail: "eventId and eventType are both required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rawPayload = JsonSerializer.Serialize(ToApplicationEvent(request));

        // TR-INT-07: acknowledgement follows persistence, not interpretation — this line is
        // what makes that true, before any of the branching below.
        var (message, isDuplicate) = await outbox.RecordInboundAsync(
            TargetSystem.Crm, "INT-CRM-EVENT", request.EventType, request.EventId, rawPayload,
            correlationContext.CorrelationId, identifier, cancellationToken).ConfigureAwait(false);

        if (!isDuplicate)
        {
            try
            {
                await inboundEventService.ApplyAsync(message.MessageId, cancellationToken).ConfigureAwait(false);
            }
            catch (InboundEventNotFoundException exception)
            {
                return Results.Problem(title: "Ticket not found", detail: exception.Message, statusCode: StatusCodes.Status404NotFound);
            }
            catch (InboundEventNotApplicableException exception)
            {
                return Results.Problem(title: "Event not applicable", detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (ActivationRequestValidationException exception)
            {
                return Results.Problem(title: "Invalid event payload", detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            catch (ActivationRequestConflictException exception)
            {
                return Results.Problem(title: "Invalid state transition", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }

        return Results.Ok(new TicketEventAccepted(request.EventId, identifier, isDuplicate, message.CreatedAt));
    }

    /// <summary>POST /api/v1/tickets/events/replay — TR-INT-31.</summary>
    /// <param name="request">Ticket identifier or time window to replay.</param>
    /// <param name="inboundEventService">Re-applies each replayed event.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    private static async Task<IResult> ReplayEvents(
        [FromBody] EventReplayRequest request,
        IInboundEventService inboundEventService,
        CancellationToken cancellationToken)
    {
        await inboundEventService.ReplayAsync(request.TicketIdentifier, request.FromUtc, request.ToUtc, cancellationToken).ConfigureAwait(false);

        return Results.Accepted();
    }

    private static InboundTicketEvent ToApplicationEvent(TicketEventRequest request) =>
        new(
            request.EventId,
            request.EventType,
            request.Identifier,
            request.CrmTicketId,
            request.OccurredAt,
            new InboundTicketEventPayload(
                request.Payload.Status,
                request.Payload.Comment,
                request.Payload.ClearingCode,
                request.Payload.ClearingText,
                request.Payload.ClosedBy,
                request.Payload.RequiresIspConfirmation,
                request.Payload.ForwardingGroup,
                request.Payload.Agent,
                request.Payload.SalesOrderId,
                request.Payload.BusinessPartner));
}
