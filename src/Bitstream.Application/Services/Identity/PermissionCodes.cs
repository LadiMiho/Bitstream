namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Constants for the permission codes seeded in <c>db/mssql/0007_seed_roles_permissions.sql</c>.
/// Endpoints reference these rather than string literals so a typo fails to compile instead of
/// silently granting or denying the wrong thing.
/// <para>
/// The codes here are the ones this module's endpoints check. The rest of the seeded catalogue
/// (activation, ticket, service change, reporting, integration codes) belongs to the modules
/// that expose those endpoints and is declared there when they are built.
/// </para>
/// </summary>
public static class PermissionCodes
{
    public const string IspCreate = "isp.create";

    public const string IspUpdate = "isp.update";

    /// <summary>Lock or unlock an ISP, cascading to all of its users (TR-SEC-13).</summary>
    public const string IspLock = "isp.lock";

    /// <summary>Read any ISP. An ISP user reads their own without this — ownership, not permission (TR-SEC-18).</summary>
    public const string IspReadAll = "isp.read.all";

    public const string UserCreate = "user.create";

    public const string UserUpdate = "user.update";

    /// <summary>Lock or unlock a user (TR-SEC-12).</summary>
    public const string UserLock = "user.lock";

    public const string RoleManage = "role.manage";

    public const string AuditRead = "audit.read";
}
