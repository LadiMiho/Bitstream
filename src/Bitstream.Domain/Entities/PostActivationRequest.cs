namespace Bitstream.Domain.Entities;

/// <summary>
/// A post-activation request (portal.PostActivationRequest, db/mssql/0024): a phone number from
/// BI, a level-3 support classification and an optional note, sent to CRM as
/// the <c>BITSTREAM_TO_CRM</c> workflow. The classification chain is copied at submission so later
/// renames never change a past request.
/// </summary>
public sealed class PostActivationRequest
{
    public long RequestId { get; set; }

    public required string PublicId { get; set; }

    public long IspId { get; set; }

    public Isp Isp { get; set; } = null!;

    public required string Phonenumber { get; set; }

    /// <summary>The level-3 classification chosen.</summary>
    public long ClassificationId { get; set; }

    public required string Classification1Code { get; set; }

    public required string Classification1Name { get; set; }

    public required string Classification2Code { get; set; }

    public required string Classification2Name { get; set; }

    /// <summary>The code CRM receives as <c>Classification</c>.</summary>
    public required string Classification3Code { get; set; }

    public required string Classification3Name { get; set; }

    public string? Note { get; set; }

    /// <summary>One of <see cref="PostActivationRequestStatus"/>.</summary>
    public required string Status { get; set; }

    /// <summary>CRM's <c>EvServiceTicketId</c>: the ticket created for this request (used in later calls about it).</summary>
    public string? CrmTicketId { get; set; }

    /// <summary>CRM's <c>Message</c>, or the failure reason.</summary>
    public string? CrmMessage { get; set; }

    /// <summary>CRM's <c>payload.reason</c> when it closed the ticket (TECHNICALLY_COMPLETED); shown on the request.</summary>
    public string? CompletionReason { get; set; }

    /// <summary>When CRM's TECHNICALLY_COMPLETED event was applied.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? CreatedBy { get; set; }

    public DateTimeOffset? LastUpdatedAt { get; set; }
}

/// <summary>Status codes of a <see cref="PostActivationRequest"/>.</summary>
public static class PostActivationRequestStatus
{
    /// <summary>Saved and queued; the outbox dispatcher has not had CRM's answer yet.</summary>
    public const string PendingCrmSync = "PendingCrmSync";

    /// <summary>CRM created the ticket and is working on it ("In process").</summary>
    public const string InProcess = "InProcess";

    /// <summary>CRM closed the ticket with a TECHNICALLY_COMPLETED event ("Technically completed").</summary>
    public const string TechnicallyCompleted = "TechnicallyCompleted";

    /// <summary>CRM created the ticket. Replaced by <see cref="InProcess"/>; db/mssql/0028 converts existing rows.</summary>
    public const string Submitted = "Submitted";

    /// <summary>CRM already had an open ticket for this phone number. Only requests sent before EvAlreadyExists became a failure (shown as Failed with CRM's EvMessage) carry this status.</summary>
    public const string AlreadyExists = "AlreadyExists";

    /// <summary>CRM refused the request, or every retry failed.</summary>
    public const string Failed = "Failed";
}
