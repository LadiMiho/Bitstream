namespace Bitstream.Domain.Entities;

/// <summary>
/// An in-app notification for one portal user (portal.UserNotification, db/mssql/0029): the
/// status of an activation or post-activation request changed. Shown under the header bell until
/// it is opened, and kept on the Notifications page afterwards.
/// </summary>
public sealed class UserNotification
{
    public long NotificationId { get; set; }

    /// <summary>The recipient (dbo.Users.Id).</summary>
    public long UserId { get; set; }

    /// <summary><see cref="NotificationEntityTypes.ActivationRequest"/> or <see cref="NotificationEntityTypes.PostActivationRequest"/>.</summary>
    public required string EntityType { get; set; }

    /// <summary>The request's portal ID, e.g. TRING001 or PAR_12.</summary>
    public required string EntityPublicId { get; set; }

    /// <summary>The status the request moved to (its stored code).</summary>
    public required string Status { get; set; }

    /// <summary>The status it moved from; null when the request was just created.</summary>
    public string? PreviousStatus { get; set; }

    /// <summary>Why, when the change carries a reason (no line, CRM's closure reason, a failure message).</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the user opened it; null while unread.</summary>
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>
/// One switch of the notification settings (portal.NotificationRule): whether a status change
/// (<see cref="EventCode"/>, e.g. <c>ActivationRequest:LineAvailable</c>) notifies an
/// <see cref="Audience"/>.
/// </summary>
public sealed class NotificationRule
{
    /// <summary><c>{EntityType}:{Status}</c>, e.g. <c>PostActivationRequest:TechnicallyCompleted</c>.</summary>
    public required string EventCode { get; set; }

    /// <summary><see cref="NotificationAudiences.IspUsers"/> or <see cref="NotificationAudiences.ServiceDesk"/>.</summary>
    public required string Audience { get; set; }

    public bool IsEnabled { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public long? UpdatedBy { get; set; }
}

/// <summary>The kinds of request a <see cref="UserNotification"/> can be about.</summary>
public static class NotificationEntityTypes
{
    public const string ActivationRequest = "ActivationRequest";

    public const string PostActivationRequest = "PostActivationRequest";
}

/// <summary>Who a <see cref="NotificationRule"/> notifies.</summary>
public static class NotificationAudiences
{
    /// <summary>The active users of the ISP the request belongs to.</summary>
    public const string IspUsers = "IspUsers";

    /// <summary>The active users with the ServiceDesk role.</summary>
    public const string ServiceDesk = "ServiceDesk";

    /// <summary>The role whose users make up <see cref="ServiceDesk"/>.</summary>
    public const string ServiceDeskRoleName = "ServiceDesk";

    public static string EventCode(string entityType, string status) => $"{entityType}:{status}";
}
