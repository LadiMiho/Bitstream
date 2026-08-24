using System.Net;
using System.Net.Http.Json;
using Bitstream.Application.Identity.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-04: a user with no authenticator key yet sees a QR code on login instead of a bare
/// code prompt, and their first valid code both generates/confirms the key (implicitly — its
/// mere existence from here on *is* the enrollment state, see <c>AuthEndpoints.LoginAsync</c>)
/// and signs them in. Replaces "read the secret off a console log" for every account after the
/// first one seeded by <c>DevelopmentBootstrapper</c>.
/// </summary>
public sealed class TwoFactorEnrollmentTests
{
    [Fact]
    public async Task First_login_returns_a_QR_code_and_the_first_valid_code_signs_in()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "enrollment-target@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000020");

            // Deliberately no ResetAuthenticatorKeyAsync here: no key exists yet, which is what
            // makes AuthEndpoints.LoginAsync treat this as a first login and return a QR code.
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
        }

        using var client = factory.CreateClient();

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var challenge = await loginResponse.Content.ReadFromJsonAsync<TwoFactorRequiredResponse>();
        Assert.NotNull(challenge);
        Assert.StartsWith("data:image/png;base64,", challenge!.QrCodeDataUri, StringComparison.Ordinal);

        string code;

        await using (var scope = factory.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            Assert.NotNull(await userManager.GetAuthenticatorKeyAsync(user));

            code = await userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider);
        }

        using var verifyResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login/verify", UriKind.Relative),
            new TwoFactorVerifyRequest(code));
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
    }

    [Fact]
    public async Task Login_stops_returning_a_QR_code_once_a_key_already_exists()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "already-enrolled@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Beta", "L00000021");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var loaded = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(loaded);
            await userManager.ResetAuthenticatorKeyAsync(loaded);
        }

        using var client = factory.CreateClient();

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var challenge = await loginResponse.Content.ReadFromJsonAsync<TwoFactorRequiredResponse>();
        Assert.NotNull(challenge);
        Assert.Null(challenge!.QrCodeDataUri);
    }
}
