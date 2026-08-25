using System.Security.Claims;
using Bitstream.Application.Identity.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bitstream.Web.Pages;

/// <summary>
/// Signs the current session out. A real page handler rather than client-side script, so
/// sign-out is a normal form POST and never itself acts as page navigation from JavaScript
/// (TR-SEC-07: the session is invalidated server-side immediately, not merely forgotten by the
/// client — see <see cref="Controllers.AuthController.Logout"/>, which does the same thing for
/// its JSON callers, though nothing calls it today). Idempotent.
/// </summary>
public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;

    public LogoutModel(SignInManager<User> signInManager, UserManager<User> userManager)
    {
        _signInManager = signInManager;
        _userManager = userManager;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        await _signInManager.SignOutAsync().ConfigureAwait(false);

        if (userId is not null)
        {
            var user = await _userManager.FindByIdAsync(userId).ConfigureAwait(false);

            if (user is not null)
            {
                // TR-SEC-07: invalidates any other copy of the cookie immediately (checked every
                // request — SecurityStampValidatorOptions.ValidationInterval is zero).
                await _userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
            }
        }

        return RedirectToPage("/Login");
    }
}
