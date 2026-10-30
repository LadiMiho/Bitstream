using Bitstream.Application.Services.Identity;
using Bitstream.Application.Services.Notifications;
using Bitstream.Domain.Entities;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Presentation;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

/// <summary>
/// In-app notifications: the header bell's data (Summary), the Notifications page and its grid,
/// opening a notification (marks it read, then shows the request), and the Notification settings
/// screen (Access Management, <c>notification.manage</c>). A user only ever sees their own
/// notifications. Scripts: <c>wwwroot/js/notifications-bell.js</c>,
/// <c>wwwroot/js/pages/notifications.js</c>, <c>wwwroot/js/pages/notification-settings.js</c>.
/// </summary>
[Route("Notifications")]
public sealed class NotificationsController : Controller
{
    /// <summary>How many unread notifications the bell's dropdown lists.</summary>
    public const int BellItems = 8;

    private readonly IUserNotificationService _service;

    public NotificationsController(IUserNotificationService service) => _service = service;

    [HttpGet("")]
    [RequireSession]
    public IActionResult Index()
    {
        ViewData["Title"] = "Notifications";
        return View();
    }

    [HttpGet("Summary")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var summary = await _service.GetSummaryAsync(BellItems, cancellationToken).ConfigureAwait(false);

        return Ok(new NotificationSummaryResponse(summary.UnreadCount, [.. summary.Latest.Select(ToResponse)]));
    }

    [HttpGet("Search")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> Search([FromQuery] string? filter, [FromQuery] int? skip, [FromQuery] int? take, CancellationToken cancellationToken)
    {
        var unreadOnly = string.Equals(filter, "unread", StringComparison.OrdinalIgnoreCase);
        var page = await _service.SearchAsync(unreadOnly, skip ?? 0, take ?? 20, cancellationToken).ConfigureAwait(false);
        var summary = await _service.GetSummaryAsync(1, cancellationToken).ConfigureAwait(false);

        return Ok(new NotificationListResponse([.. page.Items.Select(ToResponse)], page.TotalCount, summary.UnreadCount));
    }

    /// <summary>Marks the notification read and goes to the request it is about.</summary>
    [HttpGet("{notificationId:long}/Open")]
    [RequireSession]
    public async Task<IActionResult> Open(long notificationId, CancellationToken cancellationToken)
    {
        var notification = await _service.OpenAsync(notificationId, cancellationToken).ConfigureAwait(false);

        return notification is null
            ? Redirect("/Notifications")
            : LocalRedirect(NotificationPresenter.TargetUrl(notification));
    }

    [HttpPost("{notificationId:long}/Read")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> MarkRead(long notificationId, CancellationToken cancellationToken)
    {
        var notification = await _service.OpenAsync(notificationId, cancellationToken).ConfigureAwait(false);

        return notification is null ? NotFound() : NoContent();
    }

    [HttpPost("ReadAll")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        var marked = await _service.MarkAllReadAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new { marked });
    }

    [HttpGet("/AccessManagement/NotificationSettings")]
    [RequirePermission(PermissionCodes.NotificationManage)]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        ViewData["Title"] = "Notification settings";
        var settings = await _service.GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        return View(settings);
    }

    [HttpPost("/AccessManagement/NotificationSettings")]
    [RequireJsonPermission(PermissionCodes.NotificationManage)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> SaveSettings([FromBody] SaveNotificationSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _service.SaveSettingsAsync(
            [.. (request.Enabled ?? []).Select(rule => (rule.EventCode, rule.Audience))], cancellationToken).ConfigureAwait(false);

        return NoContent();
    }

    private static NotificationItemResponse ToResponse(UserNotification notification) => new(
        notification.NotificationId,
        NotificationPresenter.Title(notification),
        NotificationPresenter.Message(notification),
        notification.Detail,
        NotificationPresenter.StatusLabel(notification.EntityType, notification.Status),
        NotificationPresenter.StatusPillClass(notification.EntityType, notification.Status),
        notification.CreatedAt,
        notification.ReadAt is not null,
        NotificationPresenter.OpenUrl(notification));
}
