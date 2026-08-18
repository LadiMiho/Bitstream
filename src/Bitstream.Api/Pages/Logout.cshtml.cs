using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Pages;

/// <summary>
/// Signs the current session out. A real page handler rather than client-side script, so
/// sign-out is a normal form POST and never itself acts as page navigation from JavaScript
/// (TR-SEC-07: the session token is revoked server-side immediately, not merely forgotten by
/// the client). Idempotent, matching the equivalent API endpoint's behaviour.
/// </summary>
public sealed class LogoutModel : PageModel
{
    private readonly IIdentityService _identityService;
    private readonly IOptions<SessionOptions> _sessionOptions;

    public LogoutModel(IIdentityService identityService, IOptions<SessionOptions> sessionOptions)
    {
        _identityService = identityService;
        _sessionOptions = sessionOptions;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var cookieName = _sessionOptions.Value.CookieName;

        if (Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            await _identityService.SignOutAsync(token, cancellationToken).ConfigureAwait(false);
        }

        Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/" });

        return RedirectToPage("/Login");
    }
}
