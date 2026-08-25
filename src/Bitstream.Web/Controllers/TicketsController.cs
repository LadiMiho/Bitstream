using Bitstream.Application.Services;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// TRD 6.2 to 6.8: complaint ticket creation, comments, and the closure handshake. Ownership
/// follows the same discipline as everywhere else in the portal — an ISP user's own records need
/// no permission at all, TR-SEC-19 makes a cross-ISP read a 404, and every write is server-side
/// validated regardless of what the client sent (TR-SEC-20).
/// <para>
/// No page consumes this controller's actions yet — <c>Pages/PostActivation/Index.cshtml</c> is
/// currently a placeholder — but the JSON contract stays in place for when that screen is built,
/// and tests already exercise it.
/// </para>
/// </summary>
[Route("PostActivation/Tickets")]
public sealed class TicketsController : Controller
{
    private readonly IComplaintTicketService _ticketService;
    private readonly ITicketClosureService _closureService;

    public TicketsController(IComplaintTicketService ticketService, ITicketClosureService closureService)
    {
        _ticketService = ticketService;
        _closureService = closureService;
    }

    [HttpPost("")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreateComplaintTicketHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var ticket = await _ticketService.CreateAsync(
                new CreateComplaintTicket(request.IspId, request.LineId, request.CategoryL1, request.CategoryL2, request.CategoryL3, request.Description),
                cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(Get), new { publicId = ticket.PublicId }, ToResponse(ticket));
        }
        catch (ComplaintTicketValidationException exception)
        {
            return ValidationProblemFor(exception.Violations, exception.Message);
        }
    }

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] long? ispId,
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? createdFrom,
        [FromQuery] DateTimeOffset? createdTo,
        [FromQuery] string? categoryL1,
        [FromQuery] long? lineId,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var results = await _ticketService.SearchAsync(
            new ComplaintTicketFilter(ispId, status, createdFrom, createdTo, categoryL1, lineId,
                skip ?? 0, take is > 0 and <= 200 ? take.Value : 50),
            cancellationToken).ConfigureAwait(false);

        return Ok(results.Select(ToResponse));
    }

    [HttpGet("{publicId}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Get(string publicId, CancellationToken cancellationToken)
    {
        var ticket = await _ticketService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return ticket is null ? NotFound() : Ok(ToResponse(ticket));
    }

    [HttpGet("{ticketId:long}/comments")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> GetComments(long ticketId, CancellationToken cancellationToken)
    {
        try
        {
            var comments = await _ticketService.GetCommentsAsync(ticketId, cancellationToken).ConfigureAwait(false);
            return Ok(comments.Select(ToResponse));
        }
        catch (ComplaintTicketNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{ticketId:long}/comments")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketCommentCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> AddComment(long ticketId, [FromBody] AddTicketCommentHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var comment = await _ticketService.AddCommentAsync(ticketId, request.Body, cancellationToken).ConfigureAwait(false);
            return Created($"/PostActivation/Tickets/{ticketId}/comments/{comment.CommentId}", ToResponse(comment));
        }
        catch (ComplaintTicketNotFoundException)
        {
            return NotFound();
        }
        catch (ComplaintTicketValidationException exception)
        {
            return ValidationProblemFor([], exception.Message);
        }
    }

    [HttpPatch("{ticketId:long}/closure-decision")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketClosureDecide)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> RecordClosureDecision(long ticketId, [FromBody] RecordClosureDecisionHttpRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ClosureDecision>(request.Decision, ignoreCase: false, out var decision))
        {
            return ValidationProblemFor([], $"decision must be 'Confirmed' or 'Rejected'. Received: '{request.Decision}'.");
        }

        try
        {
            await _closureService.RecordIspDecisionAsync(ticketId, decision, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (TicketClosureNotFoundException)
        {
            return NotFound();
        }
        catch (TicketClosureValidationException exception)
        {
            return ValidationProblemFor([], exception.Message);
        }
        catch (TicketClosureConflictException exception)
        {
            return Problem(title: "Invalid state transition", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [HttpPost("{ticketId:long}/follow-up")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> RaiseFollowUp(long ticketId, [FromBody] RaiseFollowUpHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var followUp = await _closureService.RaiseFollowUpAsync(ticketId, request.Description, cancellationToken).ConfigureAwait(false);
            return CreatedAtAction(nameof(Get), new { publicId = followUp.PublicId }, ToResponse(followUp));
        }
        catch (TicketClosureNotFoundException)
        {
            return NotFound();
        }
        catch (TicketClosureValidationException exception)
        {
            return ValidationProblemFor([], exception.Message);
        }
        catch (TicketClosureConflictException exception)
        {
            return Problem(title: "Challenge window closed", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private ActionResult ValidationProblemFor(IReadOnlyList<string> violations, string fallbackMessage) =>
        ValidationProblem(
            errors: violations.Count > 0
                ? new Dictionary<string, string[]> { ["request"] = [.. violations] }
                : new Dictionary<string, string[]> { ["request"] = [fallbackMessage] });

    private static ComplaintTicketResponse ToResponse(ComplaintTicket ticket) =>
        new(ticket.TicketId, ticket.PublicId, ticket.IspId, ticket.LineId, ticket.CategoryL1, ticket.CategoryL2, ticket.CategoryL3,
            ticket.Description, ticket.Status, ticket.CrmTicketId, ticket.ClearingCode, ticket.ClearingText,
            ticket.ClosureDecision?.ToString(), ticket.ConfirmationDueAt, ticket.ParentTicketId, ticket.OpenedAt, ticket.ClosedAt);

    private static TicketCommentResponse ToResponse(TicketComment comment) =>
        new(comment.CommentId, comment.AuthorType.ToString(), comment.AuthorDisplayName, comment.Body, comment.CreatedAt);
}
