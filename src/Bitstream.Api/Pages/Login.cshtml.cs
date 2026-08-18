using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bitstream.Api.Pages;

/// <summary>
/// Sign-in page: email/password, then the 2FA challenge. The two-step flow itself is driven by
/// <c>wwwroot/js/pages/login.js</c> calling the existing
/// <see cref="Bitstream.Application.Services.IIdentityService"/>-backed endpoints
/// (<c>POST /api/v1/auth/login</c>, <c>POST /api/v1/auth/login/verify</c>) — this page only
/// renders the form and decides where <c>/Login</c> sends an already-signed-in visitor.
/// Deliberately not a <see cref="SecurePageModel"/>: guarding the page a redirect lands on
/// would loop.
/// </summary>
public sealed class LoginModel : PageModel
{
    /// <summary>Where to send the visitor once sign-in succeeds — set by <see cref="SecurePageModel"/> when it redirected here.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary>The return URL to hand to the client script, after an open-redirect check — an unrecognised or external value falls back to the module landing page.</summary>
    public string SafeReturnUrl =>
        !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/AccessManagement/Index";

    public IActionResult OnGet()
    {
        // An already-signed-in visitor has no reason to see the sign-in page again.
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect(SafeReturnUrl);
        }

        return Page();
    }
}
