using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// TRD 5: the Activation Requests grid (search/filter/browse, drawer forms for add/view/record
/// GIS outcome), plus the JSON actions those drawers' own scripts call
/// (<c>wwwroot/js/pages/activation-admin.js</c>) — mirrors <see cref="UsersController"/>/
/// <see cref="IspsController"/>'s shape.
/// <para>
/// Search and the read action enforce ownership the same way as everywhere else in the portal:
/// an Administrator/Auditor (<c>activation.read.all</c>) sees every ISP's requests; anyone else
/// sees only their own ISP's, decided from identity alone before the repository is touched
/// (TR-SEC-18, TR-SEC-19). Recording a GIS outcome needs <c>activation.gis.record</c>.
/// </para>
/// </summary>
[Route("ActivationRequests")]
public sealed class ActivationRequestsController : Controller
{
    private readonly IActivationRequestService _activationRequestService;

    public ActivationRequestsController(IActivationRequestService activationRequestService) =>
        _activationRequestService = activationRequestService;

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Activation Requests";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationCreate);
        ViewBag.CanRecordGis = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationGisRecord);

        return View();
    }

    /// <summary>
    /// The activation request submission form (TRD §5.1), posted from client-side script to
    /// <see cref="Submit"/> — nothing here re-implements validation or identifier issuance; both
    /// happen entirely server-side.
    /// <para>
    /// Package, classification and contract duration are configured lists (TR-ACT-01, TR-ACT-04 —
    /// "extensible without a release"), but there is no API that exposes that configuration to the
    /// frontend. Rather than hard-code a copy of <c>appsettings.json:Catalogues</c> here — which
    /// would silently drift the moment an administrator changed it without a redeploy — these are
    /// plain text fields; the server's own validation messages are what tell the caller a value is
    /// not in the current catalogue. Reported in docs/architecture.md.
    /// </para>
    /// </summary>
    [HttpGet("AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.ActivationCreate)]
    public IActionResult AddDrawer()
    {
        // Pre-fills the ISP ID field for an ISP user, who may only submit for their own ISP.
        ViewBag.CallerIspId = User.FindFirst(BitstreamClaimTypes.IspId)?.Value;
        return PartialView("_AddDrawer");
    }

    [HttpGet("{publicId}/ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(string publicId, CancellationToken cancellationToken)
    {
        var request = await _activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return request is null ? NotFound() : PartialView("_ViewDrawer", request);
    }

    /// <summary>
    /// The GIS verification outcome drawer (TR-ACT-12 to TR-ACT-19): opened from a specific grid
    /// row, so eligibility (status must be <c>AwaitingGisVerification</c>) is already known
    /// server-side rather than discovered by a separate lookup step. Recording the outcome itself
    /// goes through <see cref="RecordGisOutcome"/>.
    /// </summary>
    [HttpGet("{publicId}/GisOutcomeDrawer")]
    [RequirePermission(ActivationPermissionCodes.ActivationGisRecord)]
    public async Task<IActionResult> GisOutcomeDrawer(string publicId, CancellationToken cancellationToken)
    {
        var request = await _activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return request is null ? NotFound() : PartialView("_GisOutcomeDrawer", request);
    }

    // --- JSON support endpoints for the grid + drawer forms above (activation-admin.js) -----

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var result = await _activationRequestService.SearchAsync(
            search, status, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        return Ok(new ActivationRequestListResponse([.. result.Items.Select(ToSummaryResponse)], result.TotalCount));
    }

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

    private ActionResult ValidationProblemFor(ActivationRequestValidationException exception)
    {
        var errors = exception.Violations.Count > 0
            ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
            : new Dictionary<string, string[]> { ["request"] = [exception.Message] };

        return BadRequest(new ValidationProblemDetails(errors));
    }

    private static ActivationRequestResponse ToResponse(ActivationRequest request) =>
        new(request.RequestId, request.PublicId, request.IspId, request.PackageCode, request.LocationRaw,
            request.LocationLat, request.LocationLng, request.Classification, request.ContractDurationMonths,
            request.Comments, request.Status.ToString(), request.StatusReason, request.SalesOrderId,
            request.CreatedAt, request.LastUpdatedAt);

    private static ActivationRequestSummaryResponse ToSummaryResponse(ActivationRequest request) =>
        new(request.RequestId, request.PublicId, request.IspId, request.Isp.Name, request.PackageCode,
            request.Status.ToString(), request.CreatedAt);
}
