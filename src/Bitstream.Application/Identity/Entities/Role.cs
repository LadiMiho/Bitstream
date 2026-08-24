using Microsoft.AspNetCore.Identity;

namespace Bitstream.Application.Identity.Entities;

/// <summary>
/// Seeded role. TRD 3.1 "Role", roles per TRD 4.3. Backed by the standard <c>Roles</c>
/// table plus <see cref="Description"/>/<see cref="IsSystemRole"/>, added by the same EF
/// migration that creates it (see <see cref="User"/> for why this lives in
/// <c>Bitstream.Application</c> rather than <c>Bitstream.Domain</c>).
/// Role/permission assignment is administrator-configurable (TR-SEC-21).
/// </summary>
public sealed class Role : IdentityRole<long>
{
    public string? Description { get; set; }

    /// <summary>Seeded roles cannot be renamed or removed by an administrator.</summary>
    public bool IsSystemRole { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
