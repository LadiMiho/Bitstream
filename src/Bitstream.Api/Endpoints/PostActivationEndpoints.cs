using Bitstream.Api.Contracts;
using Bitstream.Api.Security;
using Bitstream.Application.Services;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// TRD 6.2 to 6.8: complaint ticket creation, comments, the closure handshake and service
/// status management. Ownership follows the same discipline as everywhere else in the
/// portal — an ISP user's own records need no permission at all, TR-SEC-19 makes a
/// cross-ISP read a 404, and every write is server-side validated regardless of what the
/// client sent (TR-SEC-20).
/// </summary>
public static class PostActivationEndpoints
{
    public static IEndpointRouteBuilder MapPostActivationEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var tickets = app.MapGroup("/api/v1/tickets")
            .WithTags("Complaint tickets")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        tickets.MapPost("/", CreateTicketAsync)
            .WithName("CreateComplaintTicket")
            .WithSummary("Raise a complaint ticket")
            .WithDescription("TR-PAS-08 to TR-PAS-12: the three-level category cascade is validated against the configured catalogue, then the ticket is replicated to CRM (INT-CRM-04).")
            .Accepts<CreateComplaintTicketHttpRequest>("application/json")
            .Produces<ComplaintTicketResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(PostActivationPermissionCodes.TicketCreate);

        tickets.MapGet("/", SearchTicketsAsync)
            .WithName("SearchComplaintTickets")
            .WithSummary("The complaints dashboard (TR-PAS-31, TR-PAS-32)")
            .WithDescription("An ISP user's results are forced to their own ISP regardless of the ispId filter; ticket.read.all sees across ISPs.")
            .Produces<ComplaintTicketResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        tickets.MapGet("/{publicId}", GetTicketAsync)
            .WithName("GetComplaintTicket")
            .WithSummary("Read a complaint ticket")
            .WithDescription("A request for another ISP's ticket returns 404, identically to one that does not exist (TR-SEC-19).")
            .Produces<ComplaintTicketResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        tickets.MapGet("/{ticketId:long}/comments", GetCommentsAsync)
            .WithName("GetTicketComments")
            .WithSummary("Read a ticket's comment thread (TRD 6.6)")
            .Produces<TicketCommentResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireAuthorization();

        tickets.MapPost("/{ticketId:long}/comments", AddCommentAsync)
            .WithName("AddTicketComment")
            .WithSummary("Add a comment to an open ticket (TRD 6.6)")
            .WithDescription("TR-PAS-27: immutable once saved. Replicated to CRM (INT-CRM-06) when the ticket has a CRM counterpart.")
            .Accepts<AddTicketCommentHttpRequest>("application/json")
            .Produces<TicketCommentResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PostActivationPermissionCodes.TicketCommentCreate);

        tickets.MapPatch("/{ticketId:long}/closure-decision", RecordClosureDecisionAsync)
            .WithName("RecordTicketClosureDecision")
            .WithSummary("Confirm or reject a proposed closure (TRD 6.4)")
            .WithDescription("Only valid from Pending ISP Confirmation (409 otherwise). \"No\" instructs CRM to reopen the ticket (TR-PAS-20).")
            .Accepts<RecordClosureDecisionHttpRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(PostActivationPermissionCodes.TicketClosureDecide);

        tickets.MapPost("/{ticketId:long}/follow-up", RaiseFollowUpAsync)
            .WithName("RaiseTicketFollowUp")
            .WithSummary("Challenge a closed ticket within the challenge window (TR-PAS-21f)")
            .Accepts<RaiseFollowUpHttpRequest>("application/json")
            .Produces<ComplaintTicketResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(PostActivationPermissionCodes.TicketCreate);

        var serviceChanges = app.MapGroup("/api/v1/service-changes")
            .WithTags("Service changes")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        serviceChanges.MapPost("/", SubmitServiceChangeAsync)
            .WithName("SubmitServiceChange")
            .WithSummary("Request an upgrade, downgrade or termination (TRD 6.8)")
            .Accepts<SubmitServiceChangeHttpRequest>("application/json")
            .Produces<ServiceChangeRequestResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(PostActivationPermissionCodes.ServiceChangeCreate);

        serviceChanges.MapGet("/eligible-packages", GetEligiblePackagesAsync)
            .WithName("GetEligibleTargetPackages")
            .WithSummary("Target packages valid for an upgrade or downgrade (TR-PAS-35)")
            .Produces<string[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> CreateTicketAsync(
        [FromBody] CreateComplaintTicketHttpRequest request,
        IComplaintTicketService ticketService,
        CancellationToken cancellationToken)
    {
        try
        {
            var ticket = await ticketService.CreateAsync(
                new CreateComplaintTicket(request.IspId, request.LineId, request.CategoryL1, request.CategoryL2, request.CategoryL3, request.Description),
                cancellationToken).ConfigureAwait(false);

            return Results.CreatedAtRoute("GetComplaintTicket", new { publicId = ticket.PublicId }, ToResponse(ticket));
        }
        catch (ComplaintTicketValidationException exception)
        {
            return ValidationProblem(exception.Violations, exception.Message);
        }
    }

    private static async Task<IResult> SearchTicketsAsync(
        [AsParameters] ComplaintTicketSearchQuery query,
        IComplaintTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var results = await ticketService.SearchAsync(
            new ComplaintTicketFilter(query.IspId, query.Status, query.CreatedFrom, query.CreatedTo, query.CategoryL1, query.LineId,
                query.Skip ?? 0, query.Take is > 0 and <= 200 ? query.Take.Value : 50),
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(results.Select(ToResponse));
    }

    private static async Task<IResult> GetTicketAsync(
        [FromRoute] string publicId, IComplaintTicketService ticketService, CancellationToken cancellationToken)
    {
        var ticket = await ticketService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return ticket is null ? Results.NotFound() : Results.Ok(ToResponse(ticket));
    }

    private static async Task<IResult> GetCommentsAsync(
        [FromRoute] long ticketId, IComplaintTicketService ticketService, CancellationToken cancellationToken)
    {
        try
        {
            var comments = await ticketService.GetCommentsAsync(ticketId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(comments.Select(ToResponse));
        }
        catch (ComplaintTicketNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> AddCommentAsync(
        [FromRoute] long ticketId, [FromBody] AddTicketCommentHttpRequest request, IComplaintTicketService ticketService, CancellationToken cancellationToken)
    {
        try
        {
            var comment = await ticketService.AddCommentAsync(ticketId, request.Body, cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/v1/tickets/{ticketId}/comments/{comment.CommentId}", ToResponse(comment));
        }
        catch (ComplaintTicketNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ComplaintTicketValidationException exception)
        {
            return ValidationProblem([], exception.Message);
        }
    }

    private static async Task<IResult> RecordClosureDecisionAsync(
        [FromRoute] long ticketId, [FromBody] RecordClosureDecisionHttpRequest request, ITicketClosureService closureService, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ClosureDecision>(request.Decision, ignoreCase: false, out var decision))
        {
            return ValidationProblem([], $"decision must be 'Confirmed' or 'Rejected'. Received: '{request.Decision}'.");
        }

        try
        {
            await closureService.RecordIspDecisionAsync(ticketId, decision, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (TicketClosureNotFoundException)
        {
            return Results.NotFound();
        }
        catch (TicketClosureValidationException exception)
        {
            return ValidationProblem([], exception.Message);
        }
        catch (TicketClosureConflictException exception)
        {
            return Results.Problem(title: "Invalid state transition", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RaiseFollowUpAsync(
        [FromRoute] long ticketId, [FromBody] RaiseFollowUpHttpRequest request, ITicketClosureService closureService, CancellationToken cancellationToken)
    {
        try
        {
            var followUp = await closureService.RaiseFollowUpAsync(ticketId, request.Description, cancellationToken).ConfigureAwait(false);
            return Results.CreatedAtRoute("GetComplaintTicket", new { publicId = followUp.PublicId }, ToResponse(followUp));
        }
        catch (TicketClosureNotFoundException)
        {
            return Results.NotFound();
        }
        catch (TicketClosureValidationException exception)
        {
            return ValidationProblem([], exception.Message);
        }
        catch (TicketClosureConflictException exception)
        {
            return Results.Problem(title: "Challenge window closed", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> SubmitServiceChangeAsync(
        [FromBody] SubmitServiceChangeHttpRequest request, IServiceChangeRequestService serviceChangeService, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ServiceChangeType>(request.ChangeType, ignoreCase: false, out var changeType))
        {
            return ValidationProblem([], $"changeType must be 'Upgrade', 'Downgrade' or 'Termination'. Received: '{request.ChangeType}'.");
        }

        try
        {
            var change = await serviceChangeService.SubmitAsync(
                request.LineId, changeType, request.PackageToBe, request.RequestedTerminationDate, cancellationToken).ConfigureAwait(false);

            return Results.Created($"/api/v1/service-changes/{change.PublicId}", ToResponse(change));
        }
        catch (ServiceChangeValidationException exception)
        {
            return ValidationProblem(exception.Violations, exception.Message);
        }
    }

    private static async Task<IResult> GetEligiblePackagesAsync(
        [FromQuery] long lineId, [FromQuery] string changeType, IServiceChangeRequestService serviceChangeService, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ServiceChangeType>(changeType, ignoreCase: false, out var parsedType))
        {
            return ValidationProblem([], $"changeType must be 'Upgrade', 'Downgrade' or 'Termination'. Received: '{changeType}'.");
        }

        var packages = await serviceChangeService.GetEligibleTargetPackagesAsync(lineId, parsedType, cancellationToken).ConfigureAwait(false);
        return Results.Ok(packages);
    }

    private static IResult ValidationProblem(IReadOnlyList<string> violations, string fallbackMessage) =>
        Results.ValidationProblem(
            violations.Count > 0
                ? new Dictionary<string, string[]> { ["request"] = [.. violations] }
                : new Dictionary<string, string[]> { ["request"] = [fallbackMessage] });

    private static ComplaintTicketResponse ToResponse(ComplaintTicket ticket) =>
        new(ticket.TicketId, ticket.PublicId, ticket.IspId, ticket.LineId, ticket.CategoryL1, ticket.CategoryL2, ticket.CategoryL3,
            ticket.Description, ticket.Status, ticket.CrmTicketId, ticket.ClearingCode, ticket.ClearingText,
            ticket.ClosureDecision?.ToString(), ticket.ConfirmationDueAt, ticket.ParentTicketId, ticket.OpenedAt, ticket.ClosedAt);

    private static TicketCommentResponse ToResponse(TicketComment comment) =>
        new(comment.CommentId, comment.AuthorType.ToString(), comment.AuthorDisplayName, comment.Body, comment.CreatedAt);

    private static ServiceChangeRequestResponse ToResponse(ServiceChangeRequest request) =>
        new(request.ChangeId, request.PublicId, request.LineId, request.ChangeType.ToString(), request.PackageAsIs,
            request.PackageToBe, request.RequestedTerminationDate, request.Status, request.CrmReference, request.CreatedAt);
}

/// <summary>Query parameters of the complaints dashboard search (TR-PAS-31/32).</summary>
public sealed class ComplaintTicketSearchQuery
{
    public long? IspId { get; set; }

    public string? Status { get; set; }

    public DateTimeOffset? CreatedFrom { get; set; }

    public DateTimeOffset? CreatedTo { get; set; }

    public string? CategoryL1 { get; set; }

    public long? LineId { get; set; }

    public int? Skip { get; set; }

    public int? Take { get; set; }
}
