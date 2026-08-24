using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Identity;

namespace Bitstream.Application.Identity.Entities;

/// <summary>
/// Portal user. TRD 3.1 "User". Backed by ASP.NET Core Identity's EF store
/// (<c>Bitstream.Infrastructure.Persistence.Identity.BitstreamIdentityDbContext</c>) — the
/// standard <c>Users</c> table plus the columns below, added by the same EF migration.
/// <para>
/// Lives in <c>Bitstream.Application</c>, not <c>Bitstream.Domain</c>: <see cref="IdentityUser{TKey}"/>
/// requires a package reference (<c>Microsoft.Extensions.Identity.Stores</c>) that Domain's
/// zero-package-reference rule forbids (<c>Bitstream.ArchitectureTests.LayeringTests.Domain_references_no_infrastructure_technology</c>).
/// Email is unique across the platform (TR-SEC-01); internal users (Administrator, Service Desk,
/// Auditor) carry no <see cref="IspId"/>.
/// </para>
/// <para>
/// <see cref="IdentityUser{TKey}.UserName"/> (inherited) is always set equal to <see cref="IdentityUser{TKey}.Email"/>
/// at creation — this app has no separate username concept. <see cref="IdentityUser{TKey}.PasswordHash"/> (inherited)
/// is the Argon2id hash (TR-SEC-02), set via <c>Argon2IdentityPasswordHasher</c>. Lockout
/// (TR-SEC-06) is Identity's own — <c>LockoutEnd</c>/<c>AccessFailedCount</c>/<c>LockoutEnabled</c>
/// (inherited) are genuinely used via <c>UserManager</c>/<c>SignInManager</c>, not shadowed by a
/// custom counter. Two-factor (TR-SEC-04) is likewise Identity's own — <c>TwoFactorEnabled</c>
/// (inherited) plus the standard token providers (<c>AspNetUserTokens</c>/<c>UserTokens</c>
/// stores the TOTP authenticator key, encrypted at rest via ASP.NET Core Data Protection — see
/// <c>BitstreamIdentityDbContext</c>). Inherited <c>PhoneNumber</c> is unused — this app keeps
/// its own <see cref="Mobile"/>, which has a different format/validation rule (E.164, TR-SEC-14)
/// than Identity's own phone-number 2FA channel expects.
/// </para>
/// <para>
/// Single role per user, by design (TRD 4.3): <see cref="RoleId"/> is a direct foreign key, not
/// ASP.NET Identity's many-to-many <c>AspNetUserRoles</c> table — that table still exists (it's
/// part of the standard schema) but is never populated; <see cref="Role"/>/<see cref="Role.RolePermissions"/>
/// remains the sole permission-check path (TR-SEC-17), unchanged by this migration.
/// </para>
/// </summary>
public sealed class User : IdentityUser<long>
{
    /// <summary>Owning ISP, or null for internal users.</summary>
    public long? IspId { get; set; }

    public Isp? Isp { get; set; }

    public required string FullName { get; set; }

    /// <summary>E.164 format (TR-SEC-14).</summary>
    public required string Mobile { get; set; }

    public long RoleId { get; set; }

    public Role Role { get; set; } = null!;

    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>Algorithm and cost parameters, so that hashes can be upgraded in place.</summary>
    public required string PasswordHashAlgorithm { get; set; }

    public DateTimeOffset? PasswordUpdatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public long? CreatedBy { get; set; }

    /// <summary>Last 5 hashes, for the no-reuse rule (TR-SEC-03).</summary>
    public ICollection<UserPasswordHistory> PasswordHistory { get; set; } = [];
}
