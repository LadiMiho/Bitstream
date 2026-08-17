using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Comment on a complaint ticket. TRD 3.1 "TicketComment", TRD 6.6.
/// Immutable once saved (TR-PAS-27); replicated to and from CRM (TR-PAS-26).
/// </summary>
public sealed class TicketComment
{
    public long CommentId { get; set; }

    public long TicketId { get; set; }

    public ComplaintTicket Ticket { get; set; } = null!;

    /// <summary>Null when the comment originated in CRM and has no portal user.</summary>
    public long? AuthorUserId { get; set; }

    public CommentAuthorType AuthorType { get; set; }

    /// <summary>Display name of the CRM author, when <see cref="AuthorUserId"/> is null.</summary>
    public string? AuthorDisplayName { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Replication state towards CRM: Pending, Sent, Failed, NotApplicable (TR-PAS-28).</summary>
    public required string CrmSyncStatus { get; set; }

    /// <summary>Set for comments that arrived from CRM, for deduplication.</summary>
    public string? CrmCommentId { get; set; }
}
