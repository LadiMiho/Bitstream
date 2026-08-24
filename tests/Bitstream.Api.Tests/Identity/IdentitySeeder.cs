using System.Net.Http.Json;
using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Seeds <see cref="BitstreamDbContext"/> directly for the HTTP-level tests, bypassing
/// <c>IAdministrationService</c> entirely (exercised by <c>AdministrationServiceTests</c>
/// instead). This isolates what each test is actually about: given a session that already
/// exists, does the authorisation pipeline behave correctly.
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

    /// <param name="lockoutEnd">Null (default) for an active user; a future timestamp to seed an already-locked one (TR-SEC-12).</param>
    public static async Task<User> AddUserAsync(
        BitstreamDbContext db,
        Role role,
        long? ispId,
        string email,
        UserStatus status = UserStatus.Active,
        DateTimeOffset? lockoutEnd = null)
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
            // Argon2PasswordHasher.Hash("Correct-Horse-Battery-Staple-9") computed once and
            // pasted here as a literal would be simplest, but hashing it fresh at seed time
            // keeps this file honest about what the password actually is.
            PasswordHash = TestPassword.Hash,
            PasswordHashAlgorithm = "Argon2id",
            TwoFactorEnabled = true,
            LockoutEnabled = true,
            LockoutEnd = lockoutEnd,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Drives the real two-step login (<c>POST /api/v1/auth/login</c>, <c>POST /api/v1/auth/login/verify</c>)
    /// so <paramref name="client"/> ends up carrying a genuine ASP.NET Core Identity authentication
    /// cookie — <c>WebApplicationFactory</c>'s client handles cookies across requests automatically
    /// (<c>WebApplicationFactoryClientOptions.HandleCookies</c> defaults to true), so nothing here
    /// sets a cookie header by hand, unlike the old opaque-session-token design.
    /// <para>
    /// Generates the user's authenticator key (as if already enrolled from a previous login, the
    /// same default the deleted <c>totpConfirmed: true</c> parameter used to provide) via
    /// <c>UserManager.ResetAuthenticatorKeyAsync</c> — a real login, at that point, needs no QR
    /// code — then computes the current valid TOTP code the same way an authenticator app would,
    /// via <c>UserManager.GenerateTwoFactorTokenAsync</c>. Assumes the configured 2FA channel is
    /// Totp (the test hosts' default) and the password is <see cref="TestPassword.PlainText"/>.
    /// </para>
    /// </summary>
    public static async Task AuthenticateAsync(HttpClient client, IServiceProvider services, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(email).ConfigureAwait(false) ??
            throw new InvalidOperationException($"No seeded user with email '{email}' — call AddUserAsync first.");

        await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText)).ConfigureAwait(false);

        loginResponse.EnsureSuccessStatusCode();

        var code = await userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider).ConfigureAwait(false);

        using var verifyResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login/verify", UriKind.Relative),
            new TwoFactorVerifyRequest(code)).ConfigureAwait(false);

        verifyResponse.EnsureSuccessStatusCode();
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
