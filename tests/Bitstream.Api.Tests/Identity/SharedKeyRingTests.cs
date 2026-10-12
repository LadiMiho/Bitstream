using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Integration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// The Data Protection key ring is kept in the shared database, so one host can read what another
/// encrypted; and a stored authenticator key that can no longer be decrypted is reset on a
/// correct password instead of failing every sign-in with a 500.
/// </summary>
public sealed class SharedKeyRingTests
{
    [Fact]
    public async Task A_value_protected_by_one_host_is_readable_by_another_sharing_the_database()
    {
        await using var api = new CrmApiFactory();
        await using var portal = new PortalApiFactory(api.DatabaseName, api.CrmGateway);

        var protectedValue = portal.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("secret");

        Assert.Equal("secret", api.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedValue));

        await using var scope = portal.Services.CreateAsyncScope();
        Assert.NotEmpty(scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().DataProtectionKeys);
    }

    [Fact]
    public async Task An_undecryptable_authenticator_key_is_reset_after_a_correct_password_and_enrollment_is_offered()
    {
        await using var api = new CrmApiFactory();
        await using var basePortal = new PortalApiFactory(api.DatabaseName, api.CrmGateway);

        // EF shares one compiled model (and its token protector) across the test hosts, so a key
        // from "another machine" cannot be produced here; a stored key with this marker value is
        // made to fail exactly as an undecryptable one does.
        await using var portal = basePortal.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddScoped<UserManager<User>, UndecryptableKeyUserManager>()));

        const string email = "rekey@example.com";

        await using (var scope = portal.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "user.read");
            await IdentitySeeder.AddUserAsync(db, role, ispId: null, email);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await userManager.FindByEmailAsync(email))!;
            await userManager.SetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey", UndecryptableKeyUserManager.Marker);
        }

        using var client = portal.CreateClient();

        // Wrong password: an ordinary 401, and the stored key is left alone.
        using (var wrong = await client.PostAsJsonAsync(new Uri("/Auth/Login", UriKind.Relative), new LoginRequest(email, "not-the-password")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        Assert.Equal(UndecryptableKeyUserManager.Marker, await ReadStoredKeyAsync(portal.Services, email));

        // Right password: the unreadable key is replaced and the QR code is shown for enrollment.
        using var login = await client.PostAsJsonAsync(new Uri("/Auth/Login", UriKind.Relative), new LoginRequest(email, TestPassword.PlainText));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var challenge = await login.Content.ReadFromJsonAsync<TwoFactorRequiredResponse>();
        Assert.Equal("Totp", challenge!.Channel);
        Assert.NotNull(challenge.QrCodeDataUri);

        var newKey = await ReadStoredKeyAsync(portal.Services, email);
        Assert.NotNull(newKey);
        Assert.NotEqual(UndecryptableKeyUserManager.Marker, newKey);

        await using (var scope = portal.Services.CreateAsyncScope())
        {
            Assert.Contains(scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().AuditLog, a => a.ActionCode == "Security.TwoFactor.KeyReset");
        }
    }

    private static async Task<string?> ReadStoredKeyAsync(IServiceProvider services, string email)
    {
        await using var scope = services.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await userManager.FindByEmailAsync(email))!;

        return await userManager.GetAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey");
    }

    /// <summary>Behaves like Identity's UserManager, except that a stored authenticator key equal to <see cref="Marker"/> cannot be "decrypted".</summary>
    private sealed class UndecryptableKeyUserManager : UserManager<User>
    {
        public const string Marker = "UNDECRYPTABLE";

        public UndecryptableKeyUserManager(
            IUserStore<User> store,
            Microsoft.Extensions.Options.IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<User> passwordHasher,
            IEnumerable<IUserValidator<User>> userValidators,
            IEnumerable<IPasswordValidator<User>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            Microsoft.Extensions.Logging.ILogger<UserManager<User>> logger)
            : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
        {
        }

        public override async Task<string?> GetAuthenticatorKeyAsync(User user)
        {
            var key = await base.GetAuthenticatorKeyAsync(user);

            return key == Marker
                ? throw new System.Security.Cryptography.CryptographicException("The key {test} was not found in the key ring.")
                : key;
        }
    }
}
