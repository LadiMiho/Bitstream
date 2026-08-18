using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Web.Pages.ActivationRequests;

/// <summary>
/// The ISP-facing request detail view: looks up one activation request by its public ID
/// against the existing <c>GET /api/v1/activation-requests/{publicId}</c> endpoint
/// (<c>wwwroot/js/pages/activation-detail.js</c>) and shows its live status, including the
/// integration-pending states (TR-ACT-11) — <c>PendingCrmSync</c> and <c>IntegrationFailed</c>
/// render just like every other status; nothing here waits for CRM to be "live" before showing
/// a newly submitted request.
/// <para>
/// There is no list endpoint for activation requests (no <c>GET /api/v1/activation-requests</c>,
/// and <c>IActivationRequestRepository</c> has no query beyond find-by-id), so this is a
/// look-up-by-ID screen rather than a browsable list. Reported in docs/architecture.md.
/// </para>
/// </summary>
public sealed class DetailModel : SecurePageModel
{
    /// <summary>Pre-fills the lookup field, e.g. arriving from the "View this request" link right after submission.</summary>
    [BindProperty(SupportsGet = true)]
    public string? PublicId { get; set; }

    public void OnGet()
    {
    }
}
