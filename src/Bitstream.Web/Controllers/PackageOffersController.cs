using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// Package offers: the package + contract duration combinations that may be ordered and the CRM
/// code (CLASS_3) each one sends on ticket creation (portal.PackageOffer). Page, drawers and the
/// JSON actions <c>wwwroot/js/pages/package-offers.js</c> calls, all gated on
/// <c>catalogue.manage</c>.
/// </summary>
[Route("ActivationRequests/PackageOffers")]
public sealed class PackageOffersController : Controller
{
    private readonly IPackageOfferService _packageOfferService;

    public PackageOffersController(IPackageOfferService packageOfferService) => _packageOfferService = packageOfferService;

    [HttpGet("")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Package offers";
        var overview = await _packageOfferService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);

        return View(overview);
    }

    [HttpGet("AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> AddDrawer(CancellationToken cancellationToken)
    {
        var overview = await _packageOfferService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);

        return PartialView("_AddDrawer", overview);
    }

    [HttpGet("{packageCode}/{contractDurationMonths:int}/EditDrawer")]
    [RequirePermission(ActivationPermissionCodes.CatalogueManage)]
    public async Task<IActionResult> EditDrawer(string packageCode, int contractDurationMonths, CancellationToken cancellationToken)
    {
        var offer = await _packageOfferService.GetAsync(packageCode, contractDurationMonths, cancellationToken).ConfigureAwait(false);

        return offer is null ? NotFound() : PartialView("_EditDrawer", offer);
    }

    // --- JSON actions (package-offers.js) ----------------------------------------------------

    [HttpPost("")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreatePackageOfferHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _packageOfferService.CreateAsync(
                new CreatePackageOfferRequest(request.PackageCode, request.ContractDurationMonths, request.OfferCode),
                cancellationToken).ConfigureAwait(false);

            return NoContent();
        }
        catch (PackageOfferValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpPut("{packageCode}/{contractDurationMonths:int}")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Update(
        string packageCode, int contractDurationMonths, [FromBody] UpdatePackageOfferHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _packageOfferService.UpdateAsync(
                packageCode, contractDurationMonths, new UpdatePackageOfferRequest(request.OfferCode), cancellationToken).ConfigureAwait(false);

            return NoContent();
        }
        catch (PackageOfferNotFoundException)
        {
            return NotFound();
        }
        catch (PackageOfferValidationException exception)
        {
            return ValidationProblemFor(exception);
        }
    }

    [HttpPatch("{packageCode}/{contractDurationMonths:int}/status")]
    [RequireJsonPermission(ActivationPermissionCodes.CatalogueManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> SetActive(
        string packageCode, int contractDurationMonths, [FromBody] SetPackageOfferActiveHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _packageOfferService.SetActiveAsync(packageCode, contractDurationMonths, request.IsActive, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (PackageOfferNotFoundException)
        {
            return NotFound();
        }
    }

    private BadRequestObjectResult ValidationProblemFor(PackageOfferValidationException exception) =>
        BadRequest(new ValidationProblemDetails(exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
}
