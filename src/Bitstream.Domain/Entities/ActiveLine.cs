namespace Bitstream.Domain.Entities;

/// <summary>
/// Active line projected from the BI reference table. TRD 3.1 "ActiveLine", TRD 6.1.
/// Synchronisation is incremental and idempotent (TR-PAS-04); this table is a projection,
/// never a system of record.
/// </summary>
public sealed class ActiveLine
{
    public long LineId { get; set; }

    public long IspId { get; set; }

    public Isp Isp { get; set; } = null!;

    /// <summary>Contract number as supplied by BI. Unique per ISP; the idempotency key of the sync.</summary>
    public required string ContractId { get; set; }

    public required string SubscriberReference { get; set; }

    /// <summary>Technology code, e.g. GPON. Filtered by configuration, not hard-coded (TR-PAS-02).</summary>
    public required string Technology { get; set; }

    public required string PackageCode { get; set; }

    /// <summary>Line status as supplied by BI; kept as a free code because BI owns the vocabulary.</summary>
    public required string Status { get; set; }

    /// <summary>Timestamp of the last successful sync that touched this row (TR-PAS-07).</summary>
    public DateTimeOffset BiSyncedAt { get; set; }

    /// <summary>Change marker supplied by BI, used for incremental sync (TR-PAS-04).</summary>
    public string? BiChangeMarker { get; set; }
}
