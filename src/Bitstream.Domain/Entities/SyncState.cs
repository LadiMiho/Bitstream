namespace Bitstream.Domain.Entities;

/// <summary>
/// One row per named synchronisation job. Currently only the BI active-lines sync
/// (TR-PAS-03, TR-PAS-07), but keyed so another scheduled sync can share the table.
/// </summary>
public sealed class SyncState
{
    public required string SyncKey { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }

    /// <summary>Failures since the last success; an alert is raised above the configured threshold (TR-PAS-07).</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Opaque cursor for the next incremental run (TR-PAS-04).</summary>
    public string? ChangeMarker { get; set; }
}
