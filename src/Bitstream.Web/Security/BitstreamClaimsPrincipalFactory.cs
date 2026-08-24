using System.Globalization;
using System.Security.Claims;
using Bitstream.Application.Identity.Entities;
using Bitstream.Hosting.Security;
using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Bitstream.Web.Security;

/// <summary>
/// Builds the claims ASP.NET Core Identity's <c>SignInManager</c> bakes into the authentication
/// cookie on every sign-in — the direct replacement for what the deleted
/// <c>SessionAuthenticationHandler</c> used to build per request. Adds <see cref="BitstreamClaimTypes.IspId"/>
/// and one <see cref="BitstreamClaimTypes.Permission"/> claim per permission the caller's role
/// grants (TR-SEC-17), and replaces Identity's own <c>Name</c> claim (which would otherwise hold
/// <c>UserName</c> — this app's own convention, always equal to <c>Email</c>) with
/// <see cref="User.FullName"/>.
/// <para>
/// <c>Role.RolePermissions</c> is not reachable from a <see cref="User"/> loaded via
/// <c>UserManager&lt;User&gt;</c> (backed by <c>BitstreamIdentityDbContext</c>, which ignores
/// that navigation — see its own doc comment): this queries it directly from
/// <see cref="BitstreamDbContext"/> instead, the same dual-mapping pattern
/// <c>BitstreamDbContext</c>'s own doc comment describes.
/// </para>
/// <para>
/// Because <c>SecurityStampValidatorOptions.ValidationInterval</c> is zero (<c>Program.cs</c>),
/// this factory runs again on every single request, not only at sign-in — which is what makes a
/// permission or lockout change visible on the caller's very next request (TR-SEC-17).
/// </para>
/// </summary>
public sealed class BitstreamClaimsPrincipalFactory : UserClaimsPrincipalFactory<User, Role>
{
    private readonly BitstreamDbContext _dbContext;

    public BitstreamClaimsPrincipalFactory(
        UserManager<User> userManager,
        RoleManager<Role> roleManager,
        IOptions<IdentityOptions> optionsAccessor,
        BitstreamDbContext dbContext)
        : base(userManager, roleManager, optionsAccessor) =>
        _dbContext = dbContext;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);

        foreach (var nameClaim in identity.FindAll(identity.NameClaimType).ToList())
        {
            identity.RemoveClaim(nameClaim);
        }

        identity.AddClaim(new Claim(identity.NameClaimType, user.FullName));

        if (user.IspId is { } ispId)
        {
            identity.AddClaim(new Claim(BitstreamClaimTypes.IspId, ispId.ToString(CultureInfo.InvariantCulture)));
        }

        var role = await _dbContext.Roles
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .AsNoTracking()
            .FirstAsync(r => r.Id == user.RoleId)
            .ConfigureAwait(false);

        // Single-role-per-user (TRD 4.3): a plain FK, not Identity's own many-to-many UserRoles
        // table (never populated) — base.GenerateClaimsAsync's own role-claim logic finds nothing
        // there, so the Role claim is added from here instead.
        identity.AddClaim(new Claim(ClaimTypes.Role, role.Name!));

        foreach (var permission in role.RolePermissions.Select(rp => rp.Permission.Code))
        {
            identity.AddClaim(new Claim(BitstreamClaimTypes.Permission, permission));
        }

        return identity;
    }
}
