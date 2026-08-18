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
/// There is no create/edit distinction here beyond status: <c>GET /api/v1/isps/{id}</c> and
/// <c>POST /api/v1/isps</c> are the only read/write operations the backend exposes for an ISP's
/// own fields. There is also no list or search endpoint — an ISP is looked up by ID, which is
/// why this screen has a "look up by ID" form rather than a browsable table. Both gaps are
/// reported back rather than compensated for here.
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
