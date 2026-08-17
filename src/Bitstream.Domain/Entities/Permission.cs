namespace Bitstream.Domain.Entities;

/// <summary>
/// Granular action code evaluated server-side on every request (TR-SEC-17). TRD 3.1 "Permission".
/// </summary>
public sealed class Permission
{
    public long PermissionId { get; set; }

    /// <summary>Action code, e.g. <c>ticket.comment.create</c>. Unique.</summary>
    public required string Code { get; set; }

    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
