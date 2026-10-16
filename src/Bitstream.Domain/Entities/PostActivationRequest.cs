namespace Bitstream.Domain.Entities;

/// <summary>
/// A post-activation request (portal.PostActivationRequest, db/mssql/0024): a phone number from
/// BI, a level-3 support classification and an optional note, sent to CRM as
/// <c>CREATE_TICKET_BYPHONENR</c>. The classification chain is copied at submission so later
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

    /// <summary>CRM's <c>TicketID</c>: the new ticket, or the existing one when CRM answered AlreadyExists.</summary>
    public string? CrmTicketId { get; set; }

    /// <summary>CRM's <c>Message</c>, or the failure reason.</summary>
    public string? CrmMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? CreatedBy { get; set; }

    public DateTimeOffset? LastUpdatedAt { get; set; }
}

/// <summary>Status codes of a <see cref="PostActivationRequest"/>.</summary>
public static class PostActivationRequestStatus
{
    /// <summary>Saved and queued; the outbox dispatcher has not had CRM's answer yet.</summary>
    public const string PendingCrmSync = "PendingCrmSync";

    /// <summary>CRM created the ticket.</summary>
    public const string Submitted = "Submitted";

    /// <summary>CRM already has an open ticket for this phone number (AlreadyExists = X).</summary>
    public const string AlreadyExists = "AlreadyExists";

    /// <summary>CRM refused the request, or every retry failed.</summary>
    public const string Failed = "Failed";
}
