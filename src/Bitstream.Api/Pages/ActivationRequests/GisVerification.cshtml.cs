using Bitstream.Api.Security;
using Bitstream.Application.Services.Activation;

namespace Bitstream.Api.Pages.ActivationRequests;

/// <summary>
/// The GIS verification admin screen (TR-ACT-12 to TR-ACT-19): looks a request up by public ID
/// (reusing the same read endpoint <see cref="Detail"/> does) to get its numeric
/// <c>requestId</c>, then — only when its status is <c>AwaitingGisVerification</c> — records
/// the outcome against the existing <c>PATCH /api/v1/activation-requests/{requestId}/gis-outcome</c>
/// endpoint (<c>wwwroot/js/pages/activation-gis.js</c>). The line-exists/no-line decision and
/// the state transition it drives both happen entirely server-side.
/// <para>
/// There is no endpoint to list requests currently awaiting verification, so an administrator
/// needs the public ID in hand (e.g. from the ISP or a submission notification) rather than
/// picking one off a queue. Reported in docs/architecture.md alongside the other read gaps.
/// </para>
/// </summary>
public sealed class GisVerificationModel : SecurePageModel
{
    public bool CanRecordGis => User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationGisRecord);

    public void OnGet()
    {
    }
}
