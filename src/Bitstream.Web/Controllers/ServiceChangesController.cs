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
/// TRD 6.8: service upgrade/downgrade/termination requests. No page consumes this controller's
/// actions yet (same placeholder-page situation as <see cref="TicketsController"/>) — the JSON
/// contract stays in place for when that screen is built, and tests already exercise it.
/// </summary>
[Route("PostActivation/ServiceChanges")]
public sealed class ServiceChangesController : Controller
{
    private readonly IServiceChangeRequestService _serviceChangeService;

    public ServiceChangesController(IServiceChangeRequestService serviceChangeService) =>
        _serviceChangeService = serviceChangeService;

    [HttpPost("")]
    [RequireJsonPermission(PostActivationPermissionCodes.ServiceChangeCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Submit([FromBody] SubmitServiceChangeHttpRequest request, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ServiceChangeType>(request.ChangeType, ignoreCase: false, out var changeType))
        {
            return ValidationProblemFor([], $"changeType must be 'Upgrade', 'Downgrade' or 'Termination'. Received: '{request.ChangeType}'.");
        }

        try
        {
            var change = await _serviceChangeService.SubmitAsync(
                request.LineId, changeType, request.PackageToBe, request.RequestedTerminationDate, cancellationToken).ConfigureAwait(false);

            return Created($"/PostActivation/ServiceChanges/{change.PublicId}", ToResponse(change));
        }
        catch (ServiceChangeValidationException exception)
        {
            return ValidationProblemFor(exception.Violations, exception.Message);
        }
    }

    [HttpGet("eligible-packages")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> GetEligiblePackages(
        [FromQuery] long lineId, [FromQuery] string changeType, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ServiceChangeType>(changeType, ignoreCase: false, out var parsedType))
        {
            return ValidationProblemFor([], $"changeType must be 'Upgrade', 'Downgrade' or 'Termination'. Received: '{changeType}'.");
        }

        var packages = await _serviceChangeService.GetEligibleTargetPackagesAsync(lineId, parsedType, cancellationToken).ConfigureAwait(false);
        return Ok(packages);
    }

    private ActionResult ValidationProblemFor(IReadOnlyList<string> violations, string fallbackMessage)
    {
        var errors = violations.Count > 0
            ? new Dictionary<string, string[]> { ["request"] = [.. violations] }
            : new Dictionary<string, string[]> { ["request"] = [fallbackMessage] };

        return BadRequest(new ValidationProblemDetails(errors));
    }

    private static ServiceChangeRequestResponse ToResponse(ServiceChangeRequest request) =>
        new(request.ChangeId, request.PublicId, request.LineId, request.ChangeType.ToString(), request.PackageAsIs,
            request.PackageToBe, request.RequestedTerminationDate, request.Status, request.CrmReference, request.CreatedAt);
}
