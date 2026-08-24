using Bitstream.Application.Identity.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// EF Core model over ASP.NET Core Identity's own schema — <c>dbo.AspNetUsers</c>,
/// <c>AspNetRoles</c>, <c>AspNetUserRoles</c>, <c>AspNetUserClaims</c>, <c>AspNetUserLogins</c>,
/// <c>AspNetRoleClaims</c>, <c>AspNetUserTokens</c> — with the standard names/schema, unmodified,
/// so they look exactly as ASP.NET Core Identity normally produces them.
/// <para>
/// Deliberately the one EF-migration-owned exception to ADR-0002 ("no EF migrations, ever"),
/// narrowed to this subsystem only: every other table in the database stays on the hand-written
/// T-SQL under <c>/db/mssql</c>, including the tables that navigate to <see cref="User"/>/
/// <see cref="Role"/> (<c>UserSession</c>, <c>TwoFactorChallenge</c>, <c>RolePermission</c>,
/// <c>UserPasswordHistory</c>) — those stay mapped by <see cref="BitstreamDbContext"/>, which
/// maps <see cref="User"/>/<see cref="Role"/> too (read/join only, no migrations of its own) so
/// those joins keep working. See <c>BitstreamDbContext</c>'s own doc comment.
/// </para>
/// <para>
/// <c>AspNetUserRoles</c> exists (part of the standard schema) but is never populated: this app
/// keeps its single-role-per-user design — a direct <see cref="User.RoleId"/> foreign key,
/// exactly as before this migration — rather than adopting Identity's many-to-many role
/// assignment.
/// </para>
/// </summary>
public sealed class BitstreamIdentityDbContext : IdentityDbContext<User, Role, long>
{
    public BitstreamIdentityDbContext(DbContextOptions<BitstreamIdentityDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.OnModelCreating(builder);

        builder.Entity<User>(user =>
        {
            user.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            user.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
            user.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            user.Property(x => x.PasswordHashAlgorithm).HasMaxLength(50).IsRequired();
            user.Property(x => x.TotpSecret).HasColumnType("varbinary(256)");
            user.Property(x => x.TotpConfirmedAt).HasColumnType("datetimeoffset(7)");
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
            user.HasIndex(x => x.IspId).HasFilter("[IspId] IS NOT NULL").HasDatabaseName("IX_AspNetUsers_IspId");

            // Single-role-per-user (see class doc): a plain FK to Role, independent of
            // AspNetUserRoles.
            user.HasOne(x => x.Role)
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<Role>(role =>
        {
            role.Property(x => x.Description).HasMaxLength(500);
            role.Ignore(x => x.RolePermissions);
        });
    }
}
