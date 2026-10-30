using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
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
/// Post-Activation Support: the list of post-activation requests, the New request drawer (phone
/// number from BI, three linked classifications, note) and its submit, which queues CRM's
/// <c>BITSTREAM_TO_CRM</c> workflow (INT-CRM-11). Script: <c>wwwroot/js/pages/post-activation.js</c>.
/// </summary>
[Route("PostActivation")]
public sealed class PostActivationController : Controller
{
    private readonly IPostActivationRequestService _service;
    private readonly IOptionsMonitor<WorkingCalendarOptions> _calendarOptions;

    public PostActivationController(IPostActivationRequestService service, IOptionsMonitor<WorkingCalendarOptions> calendarOptions)
    {
        _service = service;
        _calendarOptions = calendarOptions;
    }

    [HttpGet("")]
    [RequireSession]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Post-Activation Support";
        ViewBag.CanCreate = User.HasClaim(BitstreamClaimTypes.Permission, PostActivationPermissionCodes.TicketCreate);
        ViewBag.CanManageClassifications = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.CatalogueManage);

        // Rows are loaded by the page script from Search; the view only needs the filter choices.
        var options = await _service.GetFilterOptionsAsync(cancellationToken).ConfigureAwait(false);

        return View(options);
    }

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search(
        [FromQuery] PostActivationSearchQuery query,
        [FromQuery] int? skip,
        [FromQuery] int? take,
        CancellationToken cancellationToken)
    {
        var result = await _service.SearchAsync(ToFilter(query), skip ?? 0, Math.Clamp(take ?? 20, 1, 200), cancellationToken).ConfigureAwait(false);

        return Ok(new PostActivationRequestListResponse([.. result.Items.Select(ToSummaryResponse)], result.TotalCount));
    }

    /// <summary>
    /// Every request matching the grid's current search and filters — all pages, not only the one
    /// on screen — as an Excel workbook. Same ownership rule as <see cref="Search"/>.
    /// </summary>
    [HttpGet("Export")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Export([FromQuery] PostActivationSearchQuery query, CancellationToken cancellationToken)
    {
        var result = await _service.ExportAsync(ToFilter(query), cancellationToken).ConfigureAwait(false);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_calendarOptions.CurrentValue.TimeZoneId);
        DateTime? Local(DateTimeOffset? value) => value is { } instant ? TimeZoneInfo.ConvertTime(instant, timeZone).DateTime : null;

        var rows = result.Items.Select(r => new object?[]
        {
            r.PublicId, r.Isp.Name, r.Phonenumber,
            r.Classification1Name, r.Classification2Name, r.Classification3Code, r.Classification3Name,
            r.Note, PostActivationRequestLabels.For(r.Status), r.CrmTicketId, r.CrmMessage,
            r.CompletionReason, Local(r.CompletedAt), Local(r.CreatedAt), Local(r.LastUpdatedAt)
        });

        if (result.TotalCount > result.Items.Count)
        {
            rows = rows.Append([$"Only the first {result.Items.Count:N0} of {result.TotalCount:N0} matching requests are included. Narrow the filters to export the rest."]);
        }

        var workbook = XlsxWriter.Write(
            "Post-activation requests",
            [
                "Request", "ISP", "Phone number", "Request type", "Category", "Detail code", "Detail",
                "Note", "Status", "CRM ticket", "CRM message", "Completion reason", "Technically completed", "Submitted", "Last updated"
            ],
            rows,
            [12, 28, 18, 24, 28, 14, 36, 40, 22, 14, 40, 40, 17, 17, 17]);

        return File(workbook, XlsxWriter.ContentType, $"post-activation-requests-{Local(DateTimeOffset.UtcNow):yyyyMMdd-HHmm}.xlsx");
    }

    [HttpGet("NewDrawer")]
    [RequirePermission(PostActivationPermissionCodes.TicketCreate)]
    public async Task<IActionResult> NewDrawer(CancellationToken cancellationToken)
    {
        var options = await _service.GetFormOptionsAsync(cancellationToken).ConfigureAwait(false);

        return PartialView("_NewDrawer", options);
    }

    [HttpGet("Requests/{publicId}/ViewDrawer")]
    [RequireSession]
    public async Task<IActionResult> ViewDrawer(string publicId, CancellationToken cancellationToken)
    {
        var details = await _service.GetAsync(publicId, cancellationToken).ConfigureAwait(false);

        if (details is null)
        {
            return NotFound();
        }

        ViewBag.ShowRawData = User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationReadAll);

        return PartialView("_ViewDrawer", details);
    }

    [HttpPost("")]
    [RequireJsonPermission(PostActivationPermissionCodes.TicketCreate)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Create([FromBody] CreatePostActivationRequestHttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var created = await _service.CreateAsync(request.Phonenumber, request.ClassificationId, request.Note, cancellationToken).ConfigureAwait(false);
            return Ok(new { publicId = created.PublicId });
        }
        catch (PostActivationRequestValidationException exception)
        {
            return BadRequest(new ValidationProblemDetails(exception.FieldErrors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray())));
        }
    }

    private static PostActivationRequestSearchFilter ToFilter(PostActivationSearchQuery query) =>
        new(
            Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status,
            IspId: query.IspId,
            Classification1Code: string.IsNullOrWhiteSpace(query.Classification1Code) ? null : query.Classification1Code,
            Classification2Code: string.IsNullOrWhiteSpace(query.Classification2Code) ? null : query.Classification2Code,
            SubmittedFrom: query.SubmittedFrom,
            SubmittedBefore: query.SubmittedBefore);

    private static PostActivationRequestSummaryResponse ToSummaryResponse(PostActivationRequest r) =>
        new(r.PublicId, r.Isp.Name, r.Phonenumber, r.Classification1Name, r.Classification2Name, r.Classification3Code, r.Classification3Name,
            r.Status, PostActivationRequestLabels.For(r.Status), PostActivationRequestLabels.PillClass(r.Status), r.CrmTicketId, r.CreatedAt);

    /// <summary>The active classification tree as the drawer's script reads it: id, code, name, children.</summary>
    public static string ClassificationsJson(IReadOnlyList<SupportClassificationNode> nodes)
    {
        static object Map(SupportClassificationNode node) => new
        {
            id = node.Classification.ClassificationId,
            code = node.Classification.Code,
            name = node.Classification.Name,
            children = node.Children.Select(Map).ToList()
        };

        return JsonSerializer.Serialize(nodes.Select(Map).ToList());
    }
}
