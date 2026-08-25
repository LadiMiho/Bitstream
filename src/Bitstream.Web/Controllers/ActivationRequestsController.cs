using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// TRD 5: activation request submission and the GIS verification admin screen's JSON support —
/// called from <c>wwwroot/js/pages/activation-new.js</c>, <c>activation-detail.js</c> and
/// <c>activation-gis.js</c>. The pages themselves (<c>Pages/ActivationRequests/*.cshtml</c>) stay
/// Razor Pages; this controller exists purely for the JSON actions they call, no page of its own.
/// <para>
/// Submission and the read action are open to any authenticated caller at the route level;
/// <see cref="IActivationRequestService"/> enforces ownership from identity, before the
/// repository is touched, the same way <see cref="UsersController"/>/<see cref="IspsController"/>
/// do (TR-SEC-18, TR-SEC-19). Recording a GIS outcome is Administrator-only (TR-ACT-12).
/// </para>
/// </summary>
[Route("ActivationRequests")]
public sealed class ActivationRequestsController : Controller
{
    private readonly IActivationRequestService _activationRequestService;

    public ActivationRequestsController(IActivationRequestService activationRequestService) =>
        _activationRequestService = activationRequestService;

    [HttpPost("")]
    [RequireJsonPermission(ActivationPermissionCodes.ActivationCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Submit([FromBody] SubmitActivationHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var activationRequest = await _activationRequestService.SubmitAsync(
                new SubmitActivationRequest(
                    request.IspId, request.PackageCode, request.LocationRaw,
                    request.Classification ?? string.Empty, request.ContractDurationMonths, request.Comments),
                cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(Get), new { publicId = activationRequest.PublicId }, ToResponse(activationRequest));
        }
        catch (ActivationRequestValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpGet("{publicId}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Get(string publicId, CancellationToken cancellationToken)
    {
        var activationRequest = await _activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        // Not found and forbidden are the same response on purpose (TR-SEC-19).
        return activationRequest is null ? NotFound() : Ok(ToResponse(activationRequest));
    }

    [HttpPatch("{requestId:long}/gis-outcome")]
    [RequireJsonPermission(ActivationPermissionCodes.ActivationGisRecord)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> RecordGisOutcome(long requestId, [FromBody] GisOutcomeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _activationRequestService.RecordGisOutcomeAsync(requestId, request.LineAvailable, request.Reason, cancellationToken)
                .ConfigureAwait(false);

            return NoContent();
        }
        catch (ActivationRequestNotFoundException)
        {
            return NotFound();
        }
        catch (ActivationRequestValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
        catch (ActivationRequestConflictException exception)
        {
            return Problem(
                title: "Invalid state transition",
                detail: exception.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private ActionResult ValidationProblemFor(ActivationRequestValidationException exception) =>
        ValidationProblem(
            errors: exception.Violations.Count > 0
                ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
                : new Dictionary<string, string[]> { ["request"] = [exception.Message] });

    private static ActivationRequestResponse ToResponse(ActivationRequest request) =>
        new(request.RequestId, request.PublicId, request.IspId, request.PackageCode, request.LocationRaw,
            request.LocationLat, request.LocationLng, request.Classification, request.ContractDurationMonths,
            request.Comments, request.Status.ToString(), request.StatusReason, request.SalesOrderId,
            request.CreatedAt, request.LastUpdatedAt);
}
