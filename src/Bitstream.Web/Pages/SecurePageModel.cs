using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bitstream.Web.Pages;

/// <summary>
/// Base class for every page that requires a signed-in session.
/// <para>
/// This is the auth-guard. <see cref="PageModel"/> already implements
/// <see cref="IAsyncPageFilter"/>, so the guard is an override of its
/// <see cref="PageModel.OnPageHandlerExecutionAsync"/> rather than a separate filter type:
/// an unauthenticated visitor is redirected to <c>/Login</c> before the page's handler ever
/// runs, server-side, not by client-side script. It is presentation only (TR-SEC-20) — every
/// application service call a page's handler makes is still authorised server-side against the
/// session's actual permissions regardless of whether this guard ran (TR-SEC-17).
/// <c>/Login</c> itself does not derive from this class, or every visit to it would redirect
/// back to itself.
/// </para>
/// </summary>
public abstract class SecurePageModel : PageModel
{
    /// <inheritdoc />
    public override Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (User.Identity?.IsAuthenticated != true)
        {
            context.Result = new RedirectToPageResult("/Login", new { returnUrl = Request.Path + Request.QueryString });
            return Task.CompletedTask;
        }

        return next();
    }
}
