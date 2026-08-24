using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Seeds <see cref="BitstreamDbContext"/> directly for the HTTP-level tests, bypassing
/// <c>IAdministrationService</c>/<c>IIdentityService</c> entirely (those are exercised by
/// <c>AdministrationServiceTests</c> and by the login/2FA path itself). This isolates what each
/// test is actually about: given a session that already exists, does the authorisation pipeline
/// behave correctly.
/// </summary>
internal static class IdentitySeeder
{
    public static async Task<Role> AddRoleAsync(BitstreamDbContext db, string name, params string[] permissionCodes)
    {
        // NormalizedName would ordinarily be set by RoleManager.CreateAsync; this seeder bypasses
        // RoleManager entirely (writes directly to BitstreamDbContext), so it is set by hand —
        // otherwise RoleManager.FindByNameAsync (which AdministrationService.ResolveRoleAsync
        // uses) would never find the seeded role.
        var role = new Role { Name = name, NormalizedName = name.ToUpperInvariant(), IsSystemRole = true };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var code in permissionCodes)
        {
            var permission = new Permission { Code = code };
            db.Permissions.Add(permission);
            await db.SaveChangesAsync();

            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.PermissionId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        return role;
    }

    public static async Task<Isp> AddIspAsync(BitstreamDbContext db, string name, string nipt)
    {
        var isp = new Isp
        {
            Name = name,
            Nipt = nipt,
            ContactPerson = "Contact Person",
            ContactEmail = $"{nipt.ToLowerInvariant()}@example.com",
            ContactMobile = "+355691234567",
            CrmBpReference = $"BP-{nipt}",
            Status = IspStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Isps.Add(isp);
        await db.SaveChangesAsync();

        return isp;
    }

    public static async Task<User> AddUserAsync(
        BitstreamDbContext db,
        Role role,
        long? ispId,
        string email,
        UserStatus status = UserStatus.Active,
        int failedLoginCount = 0,
        bool totpConfirmed = true)
    {
        var normalizedEmail = email.ToUpperInvariant();

        var user = new User
        {
            IspId = ispId,
            FullName = "Test User",
            Email = email,
            // UserName/NormalizedUserName/NormalizedEmail would ordinarily be set by
            // UserManager.CreateAsync; this seeder bypasses UserManager entirely (writes
            // directly to BitstreamDbContext), so they are set by hand — otherwise
            // UserManager.FindByEmailAsync (which every login test exercises) would never find
            // the seeded user.
            UserName = email,
            NormalizedEmail = normalizedEmail,
            NormalizedUserName = normalizedEmail,
            Mobile = "+355691234567",
            RoleId = role.Id,
            Status = status,
            FailedLoginCount = failedLoginCount,
            // Argon2PasswordHasher.Hash("Correct-Horse-Battery-Staple-9") computed once and
            // pasted here as a literal would be simplest, but hashing it fresh at seed time
            // keeps this file honest about what the password actually is.
            PasswordHash = TestPassword.Hash,
            PasswordHashAlgorithm = "Argon2id",
            // The configured second-factor channel defaults to Totp (appsettings.json), and
            // IssueChallengeAsync only decrypts this when TotpConfirmedAt is null (to build the
            // enrollment QR — see TwoFactorEnrollmentTests, which seeds that case for real via
            // ITotpSecretProtector). Every other test wants an already-enrolled user, for whom a
            // placeholder is enough to reach "challenge issued" without wiring the protector's
            // real key resolution into every test host.
            TotpSecret = [1, 2, 3, 4, 5, 6, 7, 8],
            TotpConfirmedAt = totpConfirmed ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    /// <summary>Adds an already-issued, valid session and returns the raw token to set as the cookie.</summary>
    public static async Task<string> AddSessionAsync(
        BitstreamDbContext db,
        long userId,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? lastActivityAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var rawToken = TokenHashing.GenerateOpaqueToken();

        db.UserSessions.Add(new UserSession
        {
            UserId = userId,
            TokenHash = TokenHashing.Sha256Hex(rawToken),
            IssuedAt = issuedAt ?? now,
            ExpiresAt = expiresAt ?? now.AddHours(12),
            LastActivityAt = lastActivityAt ?? now
        });

        await db.SaveChangesAsync();

        return rawToken;
    }
}

/// <summary>
/// One password, hashed once per test process rather than per seeded user — Argon2id is
/// deliberately slow (TR-SEC-02), and these tests only need a hash that verifies, not a unique one.
/// </summary>
internal static class TestPassword
{
    public const string PlainText = "Correct-Horse-Battery-Staple-9";

    public static readonly string Hash = CreateHash();

    private static string CreateHash()
    {
        var options = new TestOptionsMonitor<Bitstream.Application.Configuration.PasswordPolicyOptions>(
            new Bitstream.Application.Configuration.PasswordPolicyOptions());

        return new Argon2PasswordHasher(options).Hash(PlainText);
    }
}
