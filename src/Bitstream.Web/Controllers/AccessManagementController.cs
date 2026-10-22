using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Audit;
using Bitstream.Application.Services.Identity;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Presentation;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Bitstream.Web.Controllers;

/// <summary>
/// Access Management (TRD §4) landing page and the Audit Logs screen: search, filter, view and
/// export of sec.AuditLog (TR-SEC-25), all gated on <c>audit.read</c>. Script:
/// <c>wwwroot/js/pages/audit-log.js</c>.
/// </summary>
[Route("AccessManagement")]
public sealed class AccessManagementController : Controller
{
    /// <summary>Value of the actor filter for actions without a user.</summary>
    public const string SystemActor = "system";

    private readonly IAuditLogService _auditLogService;
    private readonly IOptionsMonitor<WorkingCalendarOptions> _calendarOptions;

    public AccessManagementController(IAuditLogService auditLogService, IOptionsMonitor<WorkingCalendarOptions> calendarOptions)
    {
        _auditLogService = auditLogService;
        _calendarOptions = calendarOptions;
    }

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Access Management";
        return View();
    }

    [HttpGet("AuditLog")]
    [RequirePermission(PermissionCodes.AuditRead)]
    public async Task<IActionResult> AuditLog(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Audit Logs";
        var options = await _auditLogService.GetFilterOptionsAsync(cancellationToken).ConfigureAwait(false);

        return View(options);
    }

    [HttpGet("AuditLog/Search")]
    [RequireJsonPermission(PermissionCodes.AuditRead)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> SearchAuditLog(
        [FromQuery] AuditLogSearchQuery query, [FromQuery] int? skip, [FromQuery] int? take, CancellationToken cancellationToken)
    {
        var result = await _auditLogService.SearchAsync(ToFilter(query), skip ?? 0, Math.Clamp(take ?? 20, 1, 200), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new AuditLogListResponse(
            [.. result.Items.Select(row => new AuditLogSummaryResponse(
                row.Entry.AuditId, row.Entry.Timestamp, ActorLabel(row), row.Entry.ActorIp,
                row.Entry.ActionCode, row.Entry.EntityType, row.Entry.EntityId, row.Entry.ActorUserId is null))],
            result.TotalCount));
    }

    [HttpGet("AuditLog/{auditId:long}/ViewDrawer")]
    [RequirePermission(PermissionCodes.AuditRead)]
    public async Task<IActionResult> AuditEntryDrawer(long auditId, CancellationToken cancellationToken)
    {
        var row = await _auditLogService.GetAsync(auditId, cancellationToken).ConfigureAwait(false);

        return row is null ? NotFound() : PartialView("_AuditEntryDrawer", row);
    }

    /// <summary>Every entry matching the current search and filters — all pages — as an Excel workbook. The export is itself audited.</summary>
    [HttpGet("AuditLog/Export")]
    [RequirePermission(PermissionCodes.AuditRead)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> ExportAuditLog([FromQuery] AuditLogSearchQuery query, CancellationToken cancellationToken)
    {
        var result = await _auditLogService.ExportAsync(ToFilter(query), cancellationToken).ConfigureAwait(false);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_calendarOptions.CurrentValue.TimeZoneId);
        DateTime Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, timeZone).DateTime;

        var rows = result.Items.Select(row => new object?[]
        {
            Local(row.Entry.Timestamp), ActorLabel(row), row.ActorEmail, row.Entry.ActorIp,
            row.Entry.ActionCode, row.Entry.EntityType, row.Entry.EntityId,
            row.Entry.OldValue, row.Entry.NewValue, row.Entry.CorrelationId
        });

        if (result.TotalCount > result.Items.Count)
        {
            rows = rows.Append([$"Only the first {result.Items.Count:N0} of {result.TotalCount:N0} matching entries are included. Narrow the filters to export the rest."]);
        }

        var workbook = XlsxWriter.Write(
            "Audit log",
            ["Time", "User", "Email", "IP / job", "Action", "Entity", "Entity ID", "Before", "After", "Correlation ID"],
            rows,
            [17, 24, 28, 16, 34, 22, 16, 50, 50, 34]);

        return File(workbook, XlsxWriter.ContentType, $"audit-log-{Local(DateTimeOffset.UtcNow):yyyyMMdd-HHmm}.xlsx");
    }

    /// <summary>The user's name, or "System" for an action without a user (a background job or a CRM-driven change).</summary>
    public static string ActorLabel(AuditLogRow row) =>
        row.Entry.ActorUserId is null ? "System" : row.ActorName ?? $"User #{row.Entry.ActorUserId}";

    private static AuditLogSearchFilter ToFilter(AuditLogSearchQuery query)
    {
        var systemOnly = string.Equals(query.Actor, SystemActor, StringComparison.OrdinalIgnoreCase);
        long? actorId = !systemOnly && long.TryParse(query.Actor, out var parsed) ? parsed : null;

        return new AuditLogSearchFilter(
            Search: string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim(),
            ActionCode: string.IsNullOrWhiteSpace(query.ActionCode) ? null : query.ActionCode,
            EntityType: string.IsNullOrWhiteSpace(query.EntityType) ? null : query.EntityType,
            ActorUserId: actorId,
            SystemOnly: systemOnly,
            From: query.From,
            Before: query.Before);
    }
}
