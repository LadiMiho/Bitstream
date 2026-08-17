using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Delivery log entry for an outbound email. TRD 3.1 "Notification", TRD 8.
/// </summary>
public sealed class Notification
{
    public long NotificationId { get; set; }

    /// <summary>Externally maintained template code (TR-NTF-01).</summary>
    public required string TemplateCode { get; set; }

    /// <summary>Resolved recipient list, semicolon-separated, after distribution-group expansion (TR-NTF-02).</summary>
    public required string Recipients { get; set; }

    public required string Subject { get; set; }

    /// <summary>Rendered body as dispatched, for evidential purposes (TR-NTF-05).</summary>
    public required string BodyRendered { get; set; }

    /// <summary>Entity type this notification relates to, e.g. ActivationRequest, ComplaintTicket.</summary>
    public required string RelatedEntityType { get; set; }

    public long? RelatedEntityId { get; set; }

    /// <summary>Public identifier of the related entity, so the log stays readable after archiving.</summary>
    public string? RelatedEntityPublicId { get; set; }

    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    /// <summary>Dispatch attempts; retried up to the configured budget (TR-NTF-04).</summary>
    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }

    public string? CorrelationId { get; set; }
}
