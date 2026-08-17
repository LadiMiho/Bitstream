namespace Bitstream.Application.Services.Activation;

/// <summary>
/// Permission codes this module's endpoints check, seeded in
/// <c>db/mssql/0007_seed_roles_permissions.sql</c>. See
/// <see cref="Bitstream.Application.Services.Identity.PermissionCodes"/> for the identity
/// module's codes and the reasoning for splitting them by module.
/// </summary>
public static class ActivationPermissionCodes
{
    public const string ActivationCreate = "activation.create";

    /// <summary>Read activation requests of the caller's own ISP — not needed by an ISP user to read their own; ownership, not permission (TR-SEC-18 pattern).</summary>
    public const string ActivationReadOwn = "activation.read.own";

    /// <summary>Read activation requests of any ISP.</summary>
    public const string ActivationReadAll = "activation.read.all";

    /// <summary>Record the manual GIS verification outcome (TR-ACT-12 to TR-ACT-19).</summary>
    public const string ActivationGisRecord = "activation.gis.record";
}
