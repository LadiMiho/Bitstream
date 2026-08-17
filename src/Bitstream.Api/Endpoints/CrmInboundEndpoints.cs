using Bitstream.Api.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// Direction B of the CRM integration, TRD 7.3.2: the single versioned inbound API through
/// which CRM transmits every ticket lifecycle update (TR-INT-22).
/// <para>
/// This covers TRD 7.1 rows INT-CRM-03 (sales order notification), INT-CRM-05 (ticket
/// lifecycle events), INT-CRM-07 (closure and clearing code) and the inbound half of
/// INT-CRM-06 (comment replication) — one endpoint, distinguished by event type, exactly as
/// TR-INT-22 requires.
/// </para>
/// <para>
/// Every handler is a stub returning 501 at scaffold stage. Authentication is not yet
/// configured: the method is TRD 11.4 open item 3.
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
                Single inbound interface for every CRM-originated update (TR-INT-22): STATUS_CHANGED,
                COMMENT_ADDED, TECHNICALLY_COMPLETED, CLOSED_WITH_CLEARING_CODE, AUTO_COMPLETED,
                REOPENED. The event type list is extensible by configuration.

                Behaviour required of the implementation:
                  * The raw payload is persisted before interpretation, so a mapping defect can be
                    corrected and events replayed (TR-INT-24).
                  * 200 is returned only after the event is persisted (TR-INT-07).
                  * eventId is the deduplication key: a repeated eventId returns the original
                    outcome, re-applies nothing and re-sends no notification (TR-INT-25).
                  * occurredAt orders events per ticket; an event older than the last applied one
                    is discarded and logged, not applied (TR-INT-25, TR-PAS-17).
                  * Downstream effects — status projection, notification dispatch — run
                    asynchronously so that CRM is never held open (TR-INT-30, 95th percentile
                    under 2 seconds).
                  * Notifications apply the suppression rules of TRD 6.3: an internal forward
                    arrives on this interface but generates no ISP email (TR-INT-28).

                Open items blocking completion: the authentication method and CRM source IP
                ranges (open item 3), and the complete status and event type list, including which
                statuses are ISP-notifiable (open item 4).
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
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapPost("/events/replay", ReplayEvents)
            .WithName("ReplayTicketEvents")
            .WithSummary("Reprocess persisted CRM events for a ticket or time window")
            .WithDescription(
                """
                Administrative recovery function (TR-INT-31). Reprocesses events already persisted
                by this interface; it does not ask CRM to resend. Replay is idempotent: an event
                that was already applied does not re-apply or re-notify.
                """)
            .Accepts<EventReplayRequest>("application/json")
            .Produces<TicketEventAccepted[]>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        return app;
    }

    /// <summary>
    /// POST /api/v1/tickets/{identifier}/events — TRD 7.3.2.
    /// </summary>
    /// <param name="identifier">Portal public identifier of the ticket, e.g. ISP_1024.</param>
    /// <param name="request">The CRM event.</param>
    private static IResult SubmitEvent(
        [FromRoute] string identifier,
        [FromBody] TicketEventRequest request) =>
        NotImplemented("Inbound CRM event processing is not implemented at scaffold stage.");

    /// <summary>POST /api/v1/tickets/events/replay — TR-INT-31.</summary>
    /// <param name="request">Ticket identifier or time window to replay.</param>
    private static IResult ReplayEvents([FromBody] EventReplayRequest request) =>
        NotImplemented("Event replay is not implemented at scaffold stage.");

    private static IResult NotImplemented(string detail) =>
        TypedResults.Problem(
            title: "Not implemented",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented);
}
