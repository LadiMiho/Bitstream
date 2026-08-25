using Bitstream.Hosting.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Bitstream.Web.Security;

/// <summary>
/// MVC-controller equivalent of <c>SecurePageModel</c> (<c>Pages/SecurePageModel.cs</c>): an
/// unauthenticated visitor is redirected to <c>/Login</c> before the action ever runs,
/// server-side, not by client-side script (TR-SEC-20). Apply to a controller or action that
/// needs a signed-in session but no specific permission — <see cref="RequirePermissionAttribute"/>
/// covers the case where a permission is also required.
/// </summary>
public sealed class RequireSessionAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = LoginRedirect(context);
        }

        return Task.CompletedTask;
    }

    internal static RedirectToPageResult LoginRedirect(AuthorizationFilterContext context) =>
        new("/Login", new { returnUrl = context.HttpContext.Request.Path + context.HttpContext.Request.QueryString });
}

/// <summary>
/// MVC-controller equivalent of <c>RouteHandlerBuilder.RequirePermission</c>
/// (<c>Security/PermissionAuthorization.cs</c>), for actions that render a form or partial a
/// caller must hold a specific permission to even see (TR-SEC-17). Not a pre-registered named
/// policy per code, for the same reason as the minimal-API version: the permission catalogue is
/// seeded data (db/mssql/0007_seed_roles_permissions.sql), not fixed at startup.
/// <para>
/// This only ever gates what the caller is offered — every write these forms lead to still goes
/// through that same controller's own JSON actions (guarded by
/// <see cref="RequireJsonPermissionAttribute"/>), which re-check the same permission server-side
/// regardless (TR-SEC-20).
/// </para>
/// </summary>
public sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _permissionCode;

    public RequirePermissionAttribute(string permissionCode) => _permissionCode = permissionCode;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = RequireSessionAttribute.LoginRedirect(context);
            return Task.CompletedTask;
        }

        if (!context.HttpContext.User.HasClaim(BitstreamClaimTypes.Permission, _permissionCode))
        {
            context.Result = new ForbidResult();
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// <see cref="RequirePermissionAttribute"/>'s counterpart for JSON-returning actions (the portal's
/// own AJAX support endpoints, called via <c>fetch</c> from <c>wwwroot/js/pages/*.js</c> — not a
/// page render). An unauthenticated caller gets a <see cref="ChallengeResult"/>, not a page
/// redirect: <c>Program.cs</c>'s <c>ConfigureApplicationCookie</c> overrides
/// <c>OnRedirectToLogin</c>/<c>OnRedirectToAccessDenied</c> globally to turn the framework's own
/// Challenge/Forbid pipeline into a plain 401/403 instead of a 302, which is what
/// <c>wwwroot/js/api-client.js</c>'s error handling expects.
/// </summary>
public sealed class RequireJsonPermissionAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _permissionCode;

    public RequireJsonPermissionAttribute(string permissionCode) => _permissionCode = permissionCode;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            context.Result = new ChallengeResult();
            return Task.CompletedTask;
        }

        if (!context.HttpContext.User.HasClaim(BitstreamClaimTypes.Permission, _permissionCode))
        {
            context.Result = new ForbidResult();
        }

        return Task.CompletedTask;
    }
}
