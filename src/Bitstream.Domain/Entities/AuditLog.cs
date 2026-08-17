namespace Bitstream.Domain.Entities;

/// <summary>
/// Append-only audit record. TRD 3.1 "AuditLog", TRD 4.4.
/// No application path may update or delete a row here (TR-SEC-24); the constraint is
/// additionally enforced in the database by a trigger, see /db/mssql.
/// </summary>
public sealed class AuditLog
{
    public long AuditId { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Null for system-initiated actions such as auto-confirmation (TR-PAS-21d).</summary>
    public long? ActorUserId { get; set; }

    /// <summary>Actor IP, or the job name for system actions (TR-SEC-23).</summary>
    public string? ActorIp { get; set; }

    public required string ActionCode { get; set; }

    public required string EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Serialised previous state, sensitive fields masked (TR-INT-09).</summary>
    public string? OldValue { get; set; }

    /// <summary>Serialised new state, sensitive fields masked.</summary>
    public string? NewValue { get; set; }

    public required string CorrelationId { get; set; }
}
