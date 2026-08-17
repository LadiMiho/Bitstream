namespace Bitstream.Domain.Entities;

/// <summary>Many-to-many join of <see cref="Role"/> and <see cref="Permission"/>. TRD 3.1 "RolePermission".</summary>
public sealed class RolePermission
{
    public long RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public long PermissionId { get; set; }

    public Permission Permission { get; set; } = null!;

    public DateTimeOffset GrantedAt { get; set; }

    public long? GrantedBy { get; set; }
}
