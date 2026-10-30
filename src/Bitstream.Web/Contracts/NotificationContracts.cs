using System.Text.Json.Serialization;

namespace Bitstream.Web.Contracts;

/// <summary>One notification as the bell and the Notifications page show it.</summary>
public sealed record NotificationItemResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("statusLabel")] string StatusLabel,
    [property: JsonPropertyName("statusClass")] string StatusClass,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("read")] bool Read,
    [property: JsonPropertyName("url")] string Url);

/// <summary>The bell: the unread count and the newest unread notifications.</summary>
public sealed record NotificationSummaryResponse(
    [property: JsonPropertyName("unreadCount")] int UnreadCount,
    [property: JsonPropertyName("items")] IReadOnlyList<NotificationItemResponse> Items);

/// <summary>A page of the Notifications grid.</summary>
public sealed record NotificationListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<NotificationItemResponse> Items,
    [property: JsonPropertyName("totalCount")] int TotalCount,
    [property: JsonPropertyName("unreadCount")] int UnreadCount);

/// <summary>One switch that is on, as the Notification settings screen saves it.</summary>
public sealed record NotificationRuleRequest(
    [property: JsonPropertyName("eventCode")] string EventCode,
    [property: JsonPropertyName("audience")] string Audience);

/// <summary>The Notification settings screen's save: every switch that is on; all others are off.</summary>
public sealed record SaveNotificationSettingsRequest(
    [property: JsonPropertyName("enabled")] IReadOnlyList<NotificationRuleRequest> Enabled);
