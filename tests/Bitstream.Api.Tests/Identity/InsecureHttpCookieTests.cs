using System.Net.Http.Json;
using Bitstream.Application.Identity.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// The session cookie is Secure (TLS-only, TR-SEC-26) unless <c>Security:AllowInsecureHttp</c> is
/// set for a test server without an HTTPS binding — where a Secure cookie would be dropped by the
/// browser and sign-in would never stick.
/// </summary>
public sealed class InsecureHttpCookieTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("true", false)]
    public async Task The_session_cookie_is_Secure_unless_plain_HTTP_is_explicitly_allowed(string? allowInsecureHttp, bool expectSecure)
    {
        await using var factory = new IdentityApiFactory();
        await using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AllowInsecureHttp"] = allowInsecureHttp })));

        const string email = "cookie-check@example.com";

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "user.read");
            await IdentitySeeder.AddUserAsync(db, role, ispId: null, email);
        }

        string authenticatorKey;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await userManager.FindByEmailAsync(email))!;
            await userManager.ResetAuthenticatorKeyAsync(user);
            authenticatorKey = (await userManager.GetAuthenticatorKeyAsync(user))!;
        }

        using var client = host.CreateClient();

        using (var login = await client.PostAsJsonAsync(new Uri("/Auth/Login", UriKind.Relative), new LoginRequest(email, TestPassword.PlainText)))
        {
            login.EnsureSuccessStatusCode();
        }

        using var verify = await client.PostAsJsonAsync(
            new Uri("/Auth/Login/Verify", UriKind.Relative), new TwoFactorVerifyRequest(TotpCodeGenerator.GenerateCode(authenticatorKey)));
        verify.EnsureSuccessStatusCode();

        var sessionCookie = Assert.Single(
            verify.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith("bitstream_session", StringComparison.Ordinal));

        Assert.Equal(expectSecure, sessionCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }
}
