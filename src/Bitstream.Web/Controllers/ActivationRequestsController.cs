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
/// TRD 5: the Activation Requests hub, new-request form, detail lookup and GIS verification
/// screens, plus the JSON actions those screens' own scripts call
/// (<c>wwwroot/js/pages/activation-new.js</c>, <c>activation-detail.js</c>, <c>activation-gis.js</c>)
/// — mirrors <see cref="UsersController"/>/<see cref="IspsController"/>'s shape.
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

    /// <summary>Activation Requests (TRD §5) hub, linking to the three screens this module has.</summary>
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
    /// The activation request submission form (TRD §5.1), posted from client-side script
    /// (<c>wwwroot/js/pages/activation-new.js</c>) to <see cref="Submit"/> — nothing here
    /// re-implements validation or identifier issuance; both happen entirely server-side.
    /// <para>
    /// Package, classification and contract duration are configured lists (TR-ACT-01, TR-ACT-04 —
    /// "extensible without a release"), but there is no API that exposes that configuration to the
    /// frontend. Rather than hard-code a copy of <c>appsettings.json:Catalogues</c> here — which
    /// would silently drift the moment an administrator changed it without a redeploy — these are
    /// plain text fields; the server's own validation messages are what tell the caller a value is
    /// not in the current catalogue. Reported in docs/architecture.md.
    /// </para>
    /// </summary>
    [HttpGet("New")]
    [RequireSession]
    public IActionResult New()
    {
        ViewData["Title"] = "New Activation Request";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationCreate);
        // Pre-fills the ISP ID field for an ISP user, who may only submit for their own ISP.
        ViewBag.CallerIspId = User.FindFirst(BitstreamClaimTypes.IspId)?.Value;

        return View();
    }

    /// <summary>
    /// The ISP-facing request detail view: looks up one activation request by its public ID
    /// against <see cref="Get"/> (<c>wwwroot/js/pages/activation-detail.js</c>) and shows its live
    /// status, including the integration-pending states (TR-ACT-11) — <c>PendingCrmSync</c> and
    /// <c>IntegrationFailed</c> render just like every other status; nothing here waits for CRM to
    /// be "live" before showing a newly submitted request.
    /// <para>
    /// There is no list endpoint for activation requests (no search/browse action on this
    /// controller, and <c>IActivationRequestRepository</c> has no query beyond find-by-id), so
    /// this is a look-up-by-ID screen rather than a browsable list. Reported in docs/architecture.md.
    /// </para>
    /// </summary>
    [HttpGet("Detail")]
    [RequireSession]
    public IActionResult Detail([FromQuery] string? publicId)
    {
        ViewData["Title"] = "Activation Request";
        // Pre-fills the lookup field, e.g. arriving from the "View this request" link right after submission.
        ViewBag.PublicId = publicId;

        return View();
    }

    /// <summary>
    /// The GIS verification admin screen (TR-ACT-12 to TR-ACT-19): looks a request up by public
    /// ID (reusing the same read action <see cref="Get"/> does) to get its numeric
    /// <c>requestId</c>, then — only when its status is <c>AwaitingGisVerification</c> — records
    /// the outcome via <see cref="RecordGisOutcome"/> (<c>wwwroot/js/pages/activation-gis.js</c>).
    /// The line-exists/no-line decision and the state transition it drives both happen entirely
    /// server-side.
    /// <para>
    /// There is no endpoint to list requests currently awaiting verification, so an administrator
    /// needs the public ID in hand (e.g. from the ISP or a submission notification) rather than
    /// picking one off a queue. Reported in docs/architecture.md alongside the other read gaps.
    /// </para>
    /// </summary>
    [HttpGet("GisVerification")]
    [RequireSession]
    public IActionResult GisVerification()
    {
        ViewData["Title"] = "GIS Verification";
        ViewBag.CanRecordGis = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationGisRecord);

        return View();
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
}
