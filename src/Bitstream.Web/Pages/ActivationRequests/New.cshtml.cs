using Bitstream.Application.Services.Activation;
using Bitstream.Hosting.Security;

namespace Bitstream.Web.Pages.ActivationRequests;

/// <summary>
/// The activation request submission form (TRD §5.1), posted from client-side script
/// (<c>wwwroot/js/pages/activation-new.js</c>) to <see cref="Controllers.ActivationRequestsController.Submit"/>
/// — nothing here re-implements validation or identifier issuance; both happen entirely
/// server-side.
/// <para>
/// Package, classification and contract duration are configured lists (TR-ACT-01, TR-ACT-04 —
/// "extensible without a release"), but there is no API that exposes that configuration to the
/// frontend. Rather than hard-code a copy of <c>appsettings.json:Catalogues</c> here — which
/// would silently drift the moment an administrator changed it without a redeploy — these are
/// plain text fields; the server's own validation messages are what tell the caller a value is
/// not in the current catalogue. Reported in docs/architecture.md.
/// </para>
/// </summary>
public sealed class NewModel : SecurePageModel
{
    public bool CanCreate => User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationCreate);

    /// <summary>Pre-fills the ISP ID field for an ISP user, who may only submit for their own ISP.</summary>
    public string? CallerIspId => User.FindFirst(BitstreamClaimTypes.IspId)?.Value;

    public void OnGet()
    {
    }
}
