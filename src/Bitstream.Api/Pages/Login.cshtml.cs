using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bitstream.Api.Pages;

/// <summary>
/// Placeholder sign-in page. The auth-guard (<see cref="SecurePageModel"/>) sends every
/// unauthenticated visitor here; the real sign-in form — email/password, then the 2FA
/// challenge, against <see cref="Bitstream.Application.Services.IIdentityService"/> — is built
/// in GUI-2. Deliberately not a <see cref="SecurePageModel"/>: guarding the page a redirect
/// lands on would loop.
/// </summary>
public sealed class LoginModel : PageModel
{
    /// <summary>Where to send the visitor once the real sign-in form (GUI-2) succeeds.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        // An already-signed-in visitor has no reason to see the sign-in page again.
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/AccessManagement/Index");
        }

        return Page();
    }
}
