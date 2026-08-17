namespace Bitstream.Domain.Entities;

/// <summary>
/// Seeded role. TRD 3.1 "Role", roles per TRD 4.3.
/// Role/permission assignment is administrator-configurable (TR-SEC-21).
/// </summary>
public sealed class Role
{
    public long RoleId { get; set; }

    /// <summary>Stable code: Administrator, IspUser, ServiceDesk, Auditor.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Seeded roles cannot be renamed or removed by an administrator.</summary>
    public bool IsSystemRole { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];

    public ICollection<User> Users { get; set; } = [];
}
