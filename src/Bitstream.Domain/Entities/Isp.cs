using Bitstream.Domain.Enums;

namespace Bitstream.Domain.Entities;

/// <summary>
/// Wholesale customer of the portal. TRD 3.1 "ISP".
/// NIPT is unique (TR-SEC-15/16); records are never physically deleted (TR-DAT-07).
/// </summary>
public sealed class Isp
{
    public long IspId { get; set; }

    public required string Name { get; set; }

    /// <summary>Albanian tax identification number. Unique across the platform.</summary>
    public required string Nipt { get; set; }

    public required string ContactPerson { get; set; }

    public required string ContactEmail { get; set; }

    /// <summary>E.164 formatted (TR-SEC-14).</summary>
    public required string ContactMobile { get; set; }

    /// <summary>CRM Business Partner reference of this ISP (TR-SEC-15).</summary>
    public required string CrmBpReference { get; set; }

    public IspStatus Status { get; set; } = IspStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Administrator who created the record; null only for seed data.</summary>
    public long? CreatedBy { get; set; }

    public ICollection<User> Users { get; set; } = [];

    public ICollection<ActivationRequest> ActivationRequests { get; set; } = [];

    public ICollection<ActiveLine> ActiveLines { get; set; } = [];

    public ICollection<ComplaintTicket> ComplaintTickets { get; set; } = [];
}
