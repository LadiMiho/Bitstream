namespace Bitstream.Domain.Entities;

/// <summary>
/// One node of the three-level post-activation classification (portal.SupportClassification,
/// db/mssql/0023): level 1 (e.g. "BITSTREAM COMPLAINTS"), level 2 under it (e.g. "NETWORK"), and
/// level 3 under that (e.g. "Speed Problem"). The ISP picks one of each, in that order, on the
/// post-activation request form; the level-3 <see cref="Code"/> is what CRM receives.
/// </summary>
public sealed class SupportClassification
{
    public const int MaxLevel = 3;

    public long ClassificationId { get; set; }

    /// <summary>The CRM code, e.g. 4001440011. Unique across all levels.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>1, 2 or 3; always one more than the parent's.</summary>
    public byte Level { get; set; }

    /// <summary>Null exactly for level 1.</summary>
    public long? ParentId { get; set; }

    public SupportClassification? Parent { get; set; }

    public ICollection<SupportClassification> Children { get; set; } = [];

    /// <summary>Order among its siblings.</summary>
    public int DisplayOrder { get; set; }

    /// <summary>An inactive node, and everything under it, is not offered on the form.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}
