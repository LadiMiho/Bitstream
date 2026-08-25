using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// ISP Administration: page (create form + search/detail grid) and the JSON CRUD that grid
/// calls (<c>wwwroot/js/pages/isp-admin.js</c>) — mirrors <see cref="UsersController"/>'s shape.
/// TR-SEC-18/19: an Administrator/Auditor (isp.read.all) sees every ISP; anyone else's search and
/// by-ID lookup are narrowed to their own ISP, enforced by <see cref="IAdministrationService"/>
/// from identity alone, before the repository is ever touched.
/// </summary>
[Route("AccessManagement/Isps")]
public sealed class IspsController : Controller
{
    private readonly IAdministrationService _administrationService;

    public IspsController(IAdministrationService administrationService) => _administrationService = administrationService;

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "ISP Administration";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.IspCreate);
        ViewBag.CanLock = User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.IspLock);

        return View();
    }

    // --- JSON support endpoints for the page above (isp-admin.js) --------------------------

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] string? search,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var result = await _administrationService.SearchIspsAsync(
            search, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        return Ok(new IspListResponse([.. result.Items.Select(ToResponse)], result.TotalCount));
    }

    [HttpPost("")]
    [RequireJsonPermission(PermissionCodes.IspCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreateIspHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var isp = await _administrationService.CreateIspAsync(
                new CreateIspRequest(request.Name, request.Nipt, request.ContactPerson, request.ContactEmail, request.ContactMobile, request.CrmBpReference),
                cancellationToken).ConfigureAwait(false);

            return CreatedAtAction(nameof(Get), new { ispId = isp.IspId }, ToResponse(isp));
        }
        catch (AdministrationValidationException exception)
        {
            return ValidationProblem(
                errors: exception.FieldErrors.Count > 0
                    ? exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())
                    : exception.Violations.Count > 0
                        ? new Dictionary<string, string[]> { ["request"] = [.. exception.Violations] }
                        : new Dictionary<string, string[]> { ["request"] = [exception.Message] });
        }
    }

    [HttpGet("{ispId:long}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Get(long ispId, CancellationToken cancellationToken)
    {
        var isp = await _administrationService.GetIspAsync(ispId, cancellationToken).ConfigureAwait(false);

        // Not found and forbidden are the same response on purpose (TR-SEC-19): the service has
        // already decided, from identity alone, whether this ispId is one the caller may see.
        return isp is null ? NotFound() : Ok(ToResponse(isp));
    }

    [HttpPatch("{ispId:long}/status")]
    [RequireJsonPermission(PermissionCodes.IspLock)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> SetStatus(long ispId, [FromBody] SetStatusRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<IspStatus>(request.Status, ignoreCase: false, out var status))
        {
            return Problem(
                title: "Invalid status",
                detail: $"Status must be 'Active' or 'Locked'. Received: '{request.Status}'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await _administrationService.SetIspStatusAsync(ispId, status, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (AdministrationValidationException)
        {
            // Administrator-only endpoint: reaching this point with an unknown ispId is an
            // ordinary not-found, not a TR-SEC-19 event — the caller was already entitled to
            // look, the target just does not exist.
            return NotFound();
        }
    }

    private static IspResponse ToResponse(Isp isp) =>
        new(isp.IspId, isp.Name, isp.Nipt, isp.ContactPerson, isp.ContactEmail, isp.ContactMobile,
            isp.CrmBpReference, isp.Status.ToString(), isp.CreatedAt);
}
