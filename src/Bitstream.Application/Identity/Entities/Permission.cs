namespace Bitstream.Application.Identity.Entities;

/// <summary>
/// Granular action code evaluated server-side on every request (TR-SEC-17). TRD 3.1 "Permission".
/// Moved out of <c>Bitstream.Domain</c> alongside <see cref="Role"/> purely because
/// <see cref="RolePermissions"/> navigates to <see cref="RolePermission"/>, which navigates to
/// <see cref="Role"/> — the table itself (<c>sec.Permission</c>) stays hand-written, unmigrated.
/// </summary>
public sealed class Permission
{
    public long PermissionId { get; set; }

    /// <summary>Action code, e.g. <c>ticket.comment.create</c>. Unique.</summary>
    public required string Code { get; set; }

    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
