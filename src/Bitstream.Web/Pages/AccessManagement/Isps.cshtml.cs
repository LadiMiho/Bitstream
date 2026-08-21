using Bitstream.Application.Services.Identity;
using Bitstream.Hosting.Security;

namespace Bitstream.Web.Pages.AccessManagement;

/// <summary>
/// ISP administration. Every action here calls one of the existing
/// <c>/api/v1/isps</c> endpoints (<see cref="Bitstream.Web.Endpoints.AdministrationEndpoints"/>)
/// from client-side script (<c>wwwroot/js/pages/isp-admin.js</c>) — this page renders the form
/// and looks up the caller's permissions once, server-side, purely to decide what to show
/// (TR-SEC-17): the API re-checks every one of them on every call regardless.
/// <para>
/// There is still no create/edit distinction beyond status: <c>GET /api/v1/isps/{id}</c> and
/// <c>POST /api/v1/isps</c> are the only read/write operations the backend exposes for an ISP's
/// own fields. <c>GET /api/v1/isps</c> (search/browse) applies the same ownership narrowing as
/// the by-ID lookup — an Administrator/Auditor sees every ISP, anyone else sees only their own.
/// </para>
/// </summary>
public sealed class IspsModel : SecurePageModel
{
    public bool CanCreate => User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.IspCreate);

    public bool CanLock => User.HasClaim(BitstreamClaimTypes.Permission, PermissionCodes.IspLock);

    public void OnGet()
    {
    }
}
