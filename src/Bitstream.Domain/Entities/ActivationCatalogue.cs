namespace Bitstream.Domain.Entities;

/// <summary>
/// Bitstream package offered to ISPs (TR-ACT-01), identical for all ISPs (TRD 11.1). DB-backed
/// so the catalogue can be maintained without a release, replacing the previous
/// <c>Catalogues:Packages</c> configuration list.
/// </summary>
public sealed class Package
{
    /// <summary>Code transmitted to CRM and BI, e.g. BITSTREAM_STD.</summary>
    public required string Code { get; set; }

    /// <summary>Label shown in the interface.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Relative rank. Decides which packages are offered for an upgrade and which for a
    /// downgrade (TR-PAS-35), so ordering is data rather than a hard-coded table.
    /// </summary>
    public int Tier { get; set; }

    /// <summary>Only an active package may be selected at submission time (TRD 5.1) or offered as an upgrade/downgrade target (TR-PAS-35).</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Ticket classification offered on the activation request form (TR-ACT-04). DB-backed,
/// replacing the previous <c>Catalogues:Classifications</c>/<c>DefaultClassification</c>
/// configuration.
/// </summary>
public sealed class ActivationClassification
{
    /// <summary>Code synchronised with CRM.</summary>
    public required string Code { get; set; }

    /// <summary>Label shown in the interface.</summary>
    public required string Name { get; set; }

    /// <summary>Pre-selected on the activation form (TR-ACT-04). At most one row may have this set — enforced by a filtered unique index (db/mssql).</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Selectable contract duration, in months (TRD 5.1). DB-backed, replacing the previous
/// <c>Catalogues:ContractDurationsMonths</c> configuration list.
/// </summary>
public sealed class ContractDuration
{
    public int Months { get; set; }

    /// <summary>Label shown in the interface, e.g. "12 months".</summary>
    public required string Label { get; set; }

    public bool IsActive { get; set; } = true;
}
