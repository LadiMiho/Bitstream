using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Upgrade, downgrade or termination request on an active line. TRD 3.1 "ServiceChangeRequest", TRD 6.8.
/// </summary>
public sealed class ServiceChangeRequest
{
    public long ChangeId { get; set; }

    /// <summary>Unique change identifier transmitted to CRM (TR-PAS-37).</summary>
    public required string PublicId { get; set; }

    public long LineId { get; set; }

    public ActiveLine Line { get; set; } = null!;

    public ServiceChangeType ChangeType { get; set; }

    /// <summary>Current package, read-only in the interface (TR-PAS-34).</summary>
    public required string PackageAsIs { get; set; }

    /// <summary>Target package; null for a termination (TR-PAS-35).</summary>
    public string? PackageToBe { get; set; }

    /// <summary>Requested termination date; mandatory for a termination (TR-PAS-36).</summary>
    public DateOnly? RequestedTerminationDate { get; set; }

    /// <summary>
    /// Status code. Kept as a code because CRM owns the transaction-type vocabulary for
    /// service changes and it is not yet agreed (TRD 11.4 open item 1).
    /// </summary>
    public required string Status { get; set; }

    public string? CrmReference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? CreatedBy { get; set; }
}
