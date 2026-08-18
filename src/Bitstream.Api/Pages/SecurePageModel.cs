using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bitstream.Api.Pages;

/// <summary>
/// Base class for every page that requires a signed-in session.
/// <para>
/// This is the auth-guard, implemented as a Razor Pages page filter (<see cref="IAsyncPageFilter"/>)
/// rather than a client-side redirect: an unauthenticated visitor is sent to <c>/Login</c> before
/// the page's handler ever runs. It is presentation only (TR-SEC-20) — every application service
/// call a page's handler makes is still authorised server-side against the session's actual
/// permissions regardless of whether this guard ran (TR-SEC-17). <c>/Login</c> itself does not
/// derive from this class, or every visit to it would redirect back to itself.
/// </para>
/// </summary>
public abstract class SecurePageModel : PageModel, IAsyncPageFilter
{
    /// <inheritdoc />
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    /// <inheritdoc />
    public Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            context.Result = new RedirectToPageResult("/Login", new { returnUrl = Request.Path + Request.QueryString });
            return Task.CompletedTask;
        }

        return next();
    }
}
