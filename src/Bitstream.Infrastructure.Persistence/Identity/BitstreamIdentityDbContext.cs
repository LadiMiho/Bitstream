using Bitstream.Application.Identity.Entities;
#pragma warning disable IDE0005 // dotnet-format's analyzer misreports these as unused (see IDataProtectionProvider/IdentityUserClaim<long> etc. below) — CI has proven the build fails without them.
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
#pragma warning restore IDE0005
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// EF Core model over ASP.NET Core Identity's own schema — <c>dbo.Users</c>, <c>Roles</c>,
/// <c>UserRoles</c>, <c>UserClaims</c>, <c>UserLogins</c>, <c>RoleClaims</c>, <c>UserTokens</c> —
/// Identity's standard shape, just without its default <c>AspNet</c>-prefixed table names.
/// <para>
/// Deliberately the one EF-migration-owned exception to ADR-0002 ("no EF migrations, ever"),
/// narrowed to this subsystem only: every other table in the database stays on the hand-written
/// T-SQL under <c>/db/mssql</c>, including the tables that navigate to <see cref="User"/>/
/// <see cref="Role"/> (<c>RolePermission</c>, <c>UserPasswordHistory</c>) — those stay mapped by
/// <see cref="BitstreamDbContext"/>, which maps <see cref="User"/>/<see cref="Role"/> too
/// (read/join only, no migrations of its own) so those joins keep working. See
/// <c>BitstreamDbContext</c>'s own doc comment.
/// </para>
/// <para>
/// Login, lockout (TR-SEC-06/12) and two-factor (TR-SEC-04) are all genuinely native now —
/// <c>LockoutEnd</c>/<c>AccessFailedCount</c>/<c>TwoFactorEnabled</c> (inherited on <see cref="User"/>)
/// and Identity's own token providers, driven by <c>SignInManager&lt;User&gt;</c> in
/// <c>Bitstream.Web/Endpoints/AuthEndpoints.cs</c>. <c>UserRoles</c> exists (part of the standard
/// schema) but is never populated: this app keeps its single-role-per-user design — a direct
/// <see cref="User.RoleId"/> foreign key — rather than adopting Identity's many-to-many role
/// assignment.
/// </para>
/// </summary>
public sealed class BitstreamIdentityDbContext : IdentityDbContext<User, Role, long>
{
    private readonly IDataProtectionProvider _dataProtectionProvider;

    public BitstreamIdentityDbContext(DbContextOptions<BitstreamIdentityDbContext> options, IDataProtectionProvider dataProtectionProvider)
        : base(options) =>
        _dataProtectionProvider = dataProtectionProvider;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);

        builder.Entity<User>(user =>
        {
            user.ToTable("Users");

            user.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            user.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
            user.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            user.Property(x => x.PasswordHashAlgorithm).HasMaxLength(50).IsRequired();
            user.Property(x => x.LastLoginAt).HasColumnType("datetimeoffset(7)");
            user.Property(x => x.PasswordUpdatedAt).HasColumnType("datetimeoffset(7)");
            user.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");

            // This context knows about exactly the 7 standard Identity tables — everything else
            // User navigates to (Isp, Role.RolePermissions transitively, PasswordHistory) is
            // owned by the hand-written schema and must not be discovered/migrated from here.
            user.Ignore(x => x.Isp);
            user.Ignore(x => x.PasswordHistory);

            // TR-SEC-14: not part of Identity's own indexes (which index NormalizedEmail, kept
            // as-is), but the ISP-scoped lookups this app runs need it.
            user.HasIndex(x => x.IspId).HasFilter("[IspId] IS NOT NULL").HasDatabaseName("IX_Users_IspId");

            // Single-role-per-user (see class doc): a plain FK to Role, independent of UserRoles.
            user.HasOne(x => x.Role)
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Role>(role =>
        {
            role.ToTable("Roles");
            role.Property(x => x.Description).HasMaxLength(500);
            role.Ignore(x => x.RolePermissions);
        });

        builder.Entity<IdentityUserClaim<long>>().ToTable("UserClaims");
        builder.Entity<IdentityUserLogin<long>>().ToTable("UserLogins");
        builder.Entity<IdentityUserRole<long>>().ToTable("UserRoles");
        builder.Entity<IdentityRoleClaim<long>>().ToTable("RoleClaims");

        // TR-SEC-04: this is where the TOTP authenticator key lands (UserManager's own
        // GetAuthenticatorKeyAsync/ResetAuthenticatorKeyAsync — see AuthEndpoints.cs), plus
        // Identity's generated 2FA recovery codes and remember-this-browser tokens. Encrypted at
        // rest via ASP.NET Core's own Data Protection API — not a bespoke cipher — the same
        // building block Identity itself uses for its cookies and Default token provider.
        builder.Entity<IdentityUserToken<long>>(token =>
        {
            token.ToTable("UserTokens");

            var protector = _dataProtectionProvider.CreateProtector("Bitstream.Identity.UserTokens");

            token.Property(x => x.Value).HasConversion(new ValueConverter<string?, string?>(
                value => value == null ? null : protector.Protect(value),
                value => value == null ? null : protector.Unprotect(value)));
        });
    }
}
