using Bitstream.Api.Security;
using Bitstream.Application.Services.Identity;

namespace Bitstream.Api.Pages.AccessManagement;

/// <summary>
/// User administration. Every action calls one of the existing <c>/api/v1/users</c> endpoints
/// (<see cref="Bitstream.Api.Endpoints.AdministrationEndpoints"/>) from client-side script
/// (<c>wwwroot/js/pages/user-admin.js</c>) — this page renders the form and looks up the
/// caller's permissions once, server-side, purely to decide what to show (TR-SEC-17): the API
/// re-checks every one of them on every call regardless.
/// <para>
/// As with ISPs, there is no create/edit distinction beyond status — only
/// <c>GET /api/v1/users/{id}</c> and <c>POST /api/v1/users</c> exist for a user's own fields —
/// and no list or search endpoint, so this is a "look up by ID" screen rather than a browsable
/// table. Both gaps are reported back rather than compensated for here.
/// </para>
/// </summary>
public sealed class UsersModel : SecurePageModel
{
    /// <summary>The seeded role catalogue (db/mssql/0007_seed_roles_permissions.sql) — fixed by the TRD, not something an API lists.</summary>
    public static readonly IReadOnlyList<string> Roles = ["Administrator", "IspUser", "ServiceDesk", "Auditor"];

    public bool CanCreate => User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserCreate);

    public bool CanLock => User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.UserLock);

    public void OnGet()
    {
    }
}
