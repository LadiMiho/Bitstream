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
/// through the existing <c>/api/v1/...</c> JSON endpoints, which re-check the same permission
/// server-side regardless (TR-SEC-20).
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
