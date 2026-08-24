using Bitstream.Hosting.Security;
using Microsoft.AspNetCore.Authorization;

namespace Bitstream.Web.Security;

/// <summary>
/// A single required permission code (TR-SEC-17). Endpoints attach this through
/// <see cref="EndpointPermissionExtensions.RequirePermission"/> rather than a pre-registered
/// named policy per code, since the permission catalogue is seeded data
/// (db/mssql/0007_seed_roles_permissions.sql), not a fixed set known at startup.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permissionCode) => PermissionCode = permissionCode;

    public string PermissionCode { get; }
}

/// <summary>
/// Checks <see cref="PermissionRequirement"/> against the <see cref="BitstreamClaimTypes.Permission"/>
/// claims <see cref="BitstreamClaimsPrincipalFactory"/> put on the principal.
/// <para>
/// This is TR-SEC-17 enforced, not merely documented: every minimal-API endpoint that requires
/// a permission runs through this handler server-side, on every request — nothing here trusts
/// a client-side control being hidden (TR-SEC-20). Ownership-scoped access — an ISP user reading
/// their own ISP — is a separate check inside the application service
/// (<see cref="Bitstream.Application.Services.IAdministrationService"/>), not a permission at
/// all, because TR-SEC-19 requires that case to come back not-found rather than forbidden.
/// </para>
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (context.User.HasClaim(BitstreamClaimTypes.Permission, requirement.PermissionCode))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Minimal-API endpoint helper for attaching a <see cref="PermissionRequirement"/>.</summary>
public static class EndpointPermissionExtensions
{
    /// <summary>Requires the caller to be authenticated and to hold <paramref name="permissionCode"/>.</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permissionCode)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.RequireAuthorization(policy => policy.Requirements.Add(new PermissionRequirement(permissionCode)));
    }
}
