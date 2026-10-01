using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// Packages &amp; contract durations (portal.Package, portal.ContractDuration): page, drawers and
/// the JSON actions <c>wwwroot/js/pages/catalogue-admin.js</c> calls, all gated on
/// <c>catalogue.manage</c> — mirrors <see cref="PackageOffersController"/>.
/// </summary>
[Route("ActivationRequests/Catalogue")]
public sealed class CatalogueController : Controller
{
    private readonly ICatalogueService _catalogueService;

    public CatalogueController(ICatalogueService catalogueService) => _catalogueService = catalogueService;

    [HttpGet("")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Packages & contract durations";
        var overview = await _catalogueService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);

        return View(overview);
    }

    // --- Packages -------------------------------------------------------------------------------

    [HttpGet("Packages/AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public IActionResult PackageAddDrawer() => PartialView("_PackageAddDrawer");

    [HttpGet("Packages/{code}/EditDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> PackageEditDrawer(string code, CancellationToken cancellationToken)
    {
        var package = await _catalogueService.GetPackageAsync(code, cancellationToken).ConfigureAwait(false);

        return package is null ? NotFound() : PartialView("_PackageEditDrawer", package);
    }

    [HttpPost("Packages")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> CreatePackage([FromBody] CreatePackageHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.CreatePackageAsync(request.Code, request.Name, request.Tier, cancellationToken));

    [HttpPut("Packages/{code}")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> UpdatePackage(string code, [FromBody] UpdatePackageHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.UpdatePackageAsync(code, request.Name, request.Tier, cancellationToken));

    [HttpPatch("Packages/{code}/status")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> SetPackageActive(string code, [FromBody] SetActiveHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.SetPackageActiveAsync(code, request.IsActive, cancellationToken));

    // --- Contract durations ---------------------------------------------------------------------

    [HttpGet("Durations/AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public IActionResult DurationAddDrawer() => PartialView("_DurationAddDrawer");

    [HttpGet("Durations/{months:int}/EditDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> DurationEditDrawer(int months, CancellationToken cancellationToken)
    {
        var duration = await _catalogueService.GetContractDurationAsync(months, cancellationToken).ConfigureAwait(false);

        return duration is null ? NotFound() : PartialView("_DurationEditDrawer", duration);
    }

    [HttpPost("Durations")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> CreateDuration([FromBody] CreateContractDurationHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.CreateContractDurationAsync(request.Months, request.Label, cancellationToken));

    [HttpPut("Durations/{months:int}")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> UpdateDuration(int months, [FromBody] UpdateContractDurationHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.UpdateContractDurationAsync(months, request.Label, cancellationToken));

    [HttpPatch("Durations/{months:int}/status")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> SetDurationActive(int months, [FromBody] SetActiveHttpRequest request, CancellationToken cancellationToken) =>
        RunAsync(() => _catalogueService.SetContractDurationActiveAsync(months, request.IsActive, cancellationToken));

    /// <summary>Runs one write and maps its outcome: 204, 404 for an unknown key, 400 with field errors.</summary>
    private async Task<IActionResult> RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return NoContent();
        }
        catch (CatalogueNotFoundException)
        {
            return NotFound();
        }
        catch (CatalogueValidationException exception)
        {
            return BadRequest(new ValidationProblemDetails(exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
        }
    }
}
