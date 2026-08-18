using Bitstream.Api.Security;
using Bitstream.Application.Services.Activation;

namespace Bitstream.Api.Pages.ActivationRequests;

/// <summary>Activation Requests (TRD §5) hub, linking to the three screens this module has.</summary>
public sealed class IndexModel : SecurePageModel
{
    public bool CanCreate => User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationCreate);

    public bool CanRecordGis => User.HasClaim(BitstreamClaimTypes.Permission, ActivationPermissionCodes.ActivationGisRecord);

    public void OnGet()
    {
    }
}
