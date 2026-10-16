using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Web.Contracts;
using Bitstream.Web.Presentation;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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
/// (TR-SEC-18, TR-SEC-19). Recording a GIS outcome needs <c>activation.gis.record</c>; the
/// operator's confirmation <c>activation.confirm</c>; the service desk's final decision
/// <c>activation.servicedesk.decide</c>.
/// </para>
/// </summary>
[Route("ActivationRequests")]
public sealed class ActivationRequestsController : Controller
{
    private readonly IActivationRequestService _activationRequestService;
    private readonly IAdministrationService _administrationService;
    private readonly ICatalogueService _catalogueService;
    private readonly IOptionsMonitor<WorkingCalendarOptions> _calendarOptions;

    public ActivationRequestsController(
        IActivationRequestService activationRequestService,
        IAdministrationService administrationService,
        ICatalogueService catalogueService,
        IOptionsMonitor<WorkingCalendarOptions> calendarOptions)
    {
        _activationRequestService = activationRequestService;
        _administrationService = administrationService;
        _catalogueService = catalogueService;
        _calendarOptions = calendarOptions;
    }

    [HttpGet("")]
    [RequireSession]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        // The Filters popup's choices: every package and duration, retired ones included, since
        // older requests still carry them; the ISP list only for those who see every ISP.
        var canReadAll = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationReadAll);
        var catalogue = await _catalogueService.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        ViewBag.FilterPackages = catalogue.Packages;
        ViewBag.FilterDurations = catalogue.ContractDurations;
        ViewBag.FilterIsps = canReadAll
            ? await _activationRequestService.GetFilterIspsAsync(cancellationToken).ConfigureAwait(false)
            : null;

        ViewData["Title"] = "Activation Requests";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationCreate);
        ViewBag.CanRecordGis = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationGisRecord);
        ViewBag.CanManageCatalogue = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.CatalogueManage);
        ViewBag.CanConfirm = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationConfirm);
        ViewBag.CanServiceDeskDecide = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationServiceDeskDecide);

        return View();
    }

    /// <summary>
    /// The activation request submission form (TRD §5.1), posted from client-side script to
    /// <see cref="Submit"/> — nothing here re-implements validation or identifier issuance; both
    /// happen entirely server-side. ISP, package, classification and contract duration are all
    /// dropdowns populated from the current data (TR-ACT-01, TR-ACT-04 — "extensible without a
    /// release"): the ISP list from <see cref="IAdministrationService.SearchIspsAsync"/>, the
    /// other three from <see cref="IActivationRequestService.GetCatalogueAsync"/>, which is
    /// DB-backed (db/mssql/0017_activation_catalogues.sql) rather than configuration.
    /// </summary>
    [HttpGet("AddDrawer")]
    [RequirePermission(ActivationPermissionCodes.ActivationCreate)]
    public async Task<IActionResult> AddDrawer(CancellationToken cancellationToken)
    {
        // Pre-fills the ISP field for an ISP user, who may only submit for their own ISP.
        ViewBag.CallerIspId = User.FindFirst(BitstreamClaimTypes.IspId)?.Value;

        var isps = await _administrationService.SearchIspsAsync(
            search: null, status: null, skip: 0, take: 1000, cancellationToken).ConfigureAwait(false);
        ViewBag.Isps = isps.Items;
        ViewBag.Catalogue = await _activationRequestService.GetCatalogueAsync(cancellationToken).ConfigureAwait(false);

        return PartialView("_AddDrawer");
    }

    [HttpGet("{publicId}/ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(string publicId, CancellationToken cancellationToken)
    {
        // The main details, then the timeline of everything exchanged with CRM and every portal
        // user's action. Raw payloads only for activation.read.all.
        var details = await _activationRequestService.GetWithTimelineAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (details is not { } found)
        {
            return NotFound();
        }

        var showRaw = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationReadAll);

        return PartialView("_ViewDrawer", new ActivationRequestDetailsViewModel(found.Request, found.Timeline, showRaw));
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

    /// <summary>
    /// The operator's confirmation drawer: after CRM reports the line activated, the request's
    /// own ISP (or an Administrator) answers whether it works. <see cref="IActivationRequestService.GetByPublicIdAsync"/>
    /// already hides another ISP's request.
    /// </summary>
    [HttpGet("{publicId}/OperatorConfirmationDrawer")]
    [RequirePermission(ActivationPermissionCodes.ActivationConfirm)]
    public async Task<IActionResult> OperatorConfirmationDrawer(string publicId, CancellationToken cancellationToken)
    {
        var request = await _activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return request is null ? NotFound() : PartialView("_OperatorConfirmationDrawer", request);
    }

    /// <summary>The service desk's final decision drawer, for a request the operator reported not working.</summary>
    [HttpGet("{publicId}/ServiceDeskDecisionDrawer")]
    [RequirePermission(ActivationPermissionCodes.ActivationServiceDeskDecide)]
    public async Task<IActionResult> ServiceDeskDecisionDrawer(string publicId, CancellationToken cancellationToken)
    {
        var request = await _activationRequestService.GetByPublicIdAsync(publicId, cancellationToken).ConfigureAwait(false);

        return request is null ? NotFound() : PartialView("_ServiceDeskDecisionDrawer", request);
    }

    // --- JSON support endpoints for the grid + drawer forms above (activation-admin.js) -----

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] ActivationSearchQuery query,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var result = await _activationRequestService.SearchAsync(
            ToFilter(query), skip ?? 0, Math.Clamp(take ?? 50, 1, 200), cancellationToken).ConfigureAwait(false);

        return Ok(new ActivationRequestListResponse([.. result.Items.Select(ToSummaryResponse)], result.TotalCount));
    }

    /// <summary>
    /// Every request matching the grid's current search and filters — all pages, not only the one
    /// on screen — as an Excel workbook. Same ownership rule as <see cref="Search"/>.
    /// </summary>
    [HttpGet("Export")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Export([FromQuery] ActivationSearchQuery query, CancellationToken cancellationToken)
    {
        var result = await _activationRequestService.ExportAsync(ToFilter(query), cancellationToken).ConfigureAwait(false);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_calendarOptions.CurrentValue.TimeZoneId);
        DateTime? Local(DateTimeOffset? value) => value is { } instant ? TimeZoneInfo.ConvertTime(instant, timeZone).DateTime : null;
        static string? YesNo(bool? value) => value switch { true => "Yes", false => "No", null => null };

        var rows = result.Items.Select(r => new object?[]
        {
            r.PublicId, r.Isp.Name, r.PackageCode, r.ContractDurationMonths, r.OfferCode, r.Classification,
            ActivationStatusLabels.For(r.Status), r.StatusReason,
            r.LocationRaw, r.LocationLat, r.LocationLng,
            r.Bp, r.CrmTicketId, r.SalesOrderId,
            Local(r.CreatedAt), Local(r.LastUpdatedAt), Local(r.LineActivatedAt),
            YesNo(r.OperatorConfirmed), r.OperatorComment, YesNo(r.ServiceDeskSucceeded), r.ServiceDeskComment,
            r.Comments
        });

        if (result.TotalCount > result.Items.Count)
        {
            rows = rows.Append([$"Only the first {result.Items.Count:N0} of {result.TotalCount:N0} matching requests are included. Narrow the filters to export the rest."]);
        }

        var workbook = XlsxWriter.Write(
            "Activation requests",
            [
                "Request", "ISP", "Package", "Contract (months)", "Package + duration code", "Classification",
                "Status", "Status reason",
                "Location", "Latitude", "Longitude",
                "Business partner", "CRM ticket", "Sales order",
                "Submitted", "Last updated", "Line activated",
                "Operator confirmed", "Operator comment", "Service desk: activated", "Service desk comment",
                "Comments"
            ],
            rows,
            [14, 28, 18, 10, 14, 22, 26, 30, 40, 11, 11, 14, 14, 14, 17, 17, 17, 10, 30, 10, 30, 40]);

        var fileName = $"activation-requests-{Local(DateTimeOffset.UtcNow):yyyyMMdd-HHmm}.xlsx";

        return File(workbook, XlsxWriter.ContentType, fileName);
    }

    private static ActivationRequestSearchFilter ToFilter(ActivationSearchQuery query) =>
        new(
            Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            Status: Enum.TryParse<ActivationRequestStatus>(query.Status, ignoreCase: false, out var status) ? status : null,
            IspId: query.IspId,
            PackageCode: string.IsNullOrWhiteSpace(query.PackageCode) ? null : query.PackageCode,
            ContractDurationMonths: query.ContractDurationMonths,
            SubmittedFrom: query.SubmittedFrom,
            SubmittedBefore: query.SubmittedBefore);

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

    [HttpPatch("{requestId:long}/operator-confirmation")]
    [RequireJsonPermission(ActivationPermissionCodes.ActivationConfirm)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> RecordOperatorConfirmation(long requestId, [FromBody] OperatorConfirmationRequest request, CancellationToken cancellationToken) =>
        RunDecisionAsync(() => _activationRequestService.RecordOperatorConfirmationAsync(requestId, request.Working, request.Comment, cancellationToken));

    [HttpPatch("{requestId:long}/service-desk-decision")]
    [RequireJsonPermission(ActivationPermissionCodes.ActivationServiceDeskDecide)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public Task<IActionResult> RecordServiceDeskDecision(long requestId, [FromBody] ServiceDeskDecisionRequest request, CancellationToken cancellationToken) =>
        RunDecisionAsync(() => _activationRequestService.RecordServiceDeskDecisionAsync(requestId, request.Success, request.Comment, cancellationToken));

    /// <summary>Runs a status-changing decision and maps its exceptions the same way as <see cref="RecordGisOutcome"/>.</summary>
    private async Task<IActionResult> RunDecisionAsync(Func<Task> decision)
    {
        try
        {
            await decision().ConfigureAwait(false);
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
        // TR-NFR-12: each message keyed by the field it concerns, so the drawer can show it next
        // to that field instead of a single combined banner.
        var errors = exception.FieldErrors.Count > 0
            ? exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())
            : exception.Violations.Count > 0
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
            request.Status.ToString(), request.CreatedAt, request.ContractDurationMonths, request.CrmTicketId);
}
