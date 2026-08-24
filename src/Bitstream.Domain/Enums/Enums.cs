namespace Bitstream.Domain.Enums;

/// <summary>Lifecycle status of an <see cref="Entities.Isp"/>. TR-DAT-07: no physical delete.</summary>
public enum IspStatus
{
    Active,
    Locked
}

/// <summary>
/// Lifecycle status of a portal user (<c>Bitstream.Application.Identity.Entities.User</c>).
/// TR-SEC-11, TR-DAT-07 (no physical delete). "Locked" (TR-SEC-12) is deliberately not a value
/// here — it is a derived condition (<c>UserManager.IsLockedOutAsync</c>, backed by Identity's
/// own <c>LockoutEnd</c>), not stored state; only the soft-delete distinction needs its own value.
/// </summary>
public enum UserStatus
{
    Active,

    /// <summary>Soft-deleted: cannot authenticate, hidden from search/browse by default, row and every audit/session/history reference stays intact.</summary>
    Deleted
}

/// <summary>
/// Activation request state machine, TRD 5.3. Values are persisted as their names.
/// Permitted transitions are declared in <see cref="ActivationRequestTransitions"/>.
/// </summary>
public enum ActivationRequestStatus
{
    Submitted,
    PendingCrmSync,
    AwaitingGisVerification,
    RejectedNoLine,
    LineAvailable,
    SalesOrderOpened,
    InProvisioning,
    Closed,
    Completed,

    /// <summary>Retry budget exhausted or CRM business rejection (TR-INT-19).</summary>
    IntegrationFailed
}

/// <summary>Outcome of the ticket closure handshake, TRD 6.4 / 6.5.</summary>
public enum ClosureDecision
{
    /// <summary>ISP pressed Confirm.</summary>
    Confirmed,

    /// <summary>ISP pressed No; CRM is instructed to reopen.</summary>
    Rejected,

    /// <summary>No decision within the configured period (TR-PAS-21c) — distinct from <see cref="Confirmed"/>.</summary>
    AutoConfirmed,

    /// <summary>CRM completed the ticket without asking for confirmation (TR-PAS-22).</summary>
    CompletedByCrm
}

/// <summary>
/// Second-factor delivery channel, TR-SEC-04 / TR-SEC-05. Configurable per environment;
/// production must not fall back to a channel weaker than the configured one. The channel to
/// use in production is TRD 11.4 open item 13.
/// </summary>
public enum TwoFactorChannel
{
    /// <summary>Time-based one-time code from an authenticator app (RFC 6238). Needs no delivery channel.</summary>
    Totp,

    /// <summary>One-time code emailed through the SMTP relay (TRD 7.1 INT-MAIL-01).</summary>
    EmailOtp,

    /// <summary>One-time code by SMS. Not implemented: no SMS provider is named anywhere in the TRD (open item 13).</summary>
    SmsOtp
}

/// <summary>Author category of a ticket comment, TRD 3.1 / TR-PAS-27.</summary>
public enum CommentAuthorType
{
    Isp,
    ServiceDesk,
    Crm
}

/// <summary>Service change types offered on an active line, TR-PAS-33.</summary>
public enum ServiceChangeType
{
    Upgrade,
    Downgrade,
    Termination
}

/// <summary>Delivery state of a notification, TR-NTF-04 / TR-NTF-05.</summary>
public enum NotificationStatus
{
    Pending,
    Sent,
    Failed
}

/// <summary>Direction of an integration message; the same table is outbox and inbox (TRD 3.1).</summary>
public enum IntegrationDirection
{
    Outbound,
    Inbound
}

/// <summary>External system an integration message belongs to, TRD 7.1.</summary>
public enum TargetSystem
{
    Crm,
    Bi,
    Sap,
    Smtp
}

/// <summary>Dispatch state of an integration message, TR-INT-04 / TR-INT-05.</summary>
public enum IntegrationMessageStatus
{
    Pending,
    InFlight,
    Succeeded,

    /// <summary>Retryable technical failure; <c>NextRetryAt</c> is set.</summary>
    Failed,

    /// <summary>Retry budget exhausted or non-retryable rejection; awaits administrator replay.</summary>
    DeadLettered
}

/// <summary>
/// Permitted transitions of the activation request state machine, TRD 5.3.
/// Declared here so that the rule lives with the domain rather than in a service.
/// </summary>
public static class ActivationRequestTransitions
{
    private static readonly Dictionary<ActivationRequestStatus, ActivationRequestStatus[]> Map = new()
    {
        [ActivationRequestStatus.Submitted] =
        [
            ActivationRequestStatus.PendingCrmSync,
            ActivationRequestStatus.AwaitingGisVerification
        ],
        [ActivationRequestStatus.PendingCrmSync] =
        [
            ActivationRequestStatus.AwaitingGisVerification,
            ActivationRequestStatus.IntegrationFailed
        ],
        [ActivationRequestStatus.AwaitingGisVerification] =
        [
            ActivationRequestStatus.RejectedNoLine,
            ActivationRequestStatus.LineAvailable
        ],
        [ActivationRequestStatus.RejectedNoLine] = [ActivationRequestStatus.Closed],
        [ActivationRequestStatus.LineAvailable] = [ActivationRequestStatus.SalesOrderOpened],
        [ActivationRequestStatus.SalesOrderOpened] = [ActivationRequestStatus.InProvisioning],
        [ActivationRequestStatus.InProvisioning] = [ActivationRequestStatus.Completed],
        [ActivationRequestStatus.IntegrationFailed] = [ActivationRequestStatus.PendingCrmSync],
        [ActivationRequestStatus.Closed] = [],
        [ActivationRequestStatus.Completed] = []
    };

    /// <summary>Returns the states reachable from <paramref name="from"/>.</summary>
    public static IReadOnlyList<ActivationRequestStatus> PermittedFrom(ActivationRequestStatus from) =>
        Map.TryGetValue(from, out var to) ? to : [];

    /// <summary>True when <paramref name="to"/> is a permitted successor of <paramref name="from"/>.</summary>
    public static bool IsPermitted(ActivationRequestStatus from, ActivationRequestStatus to) =>
        PermittedFrom(from).Contains(to);
}
