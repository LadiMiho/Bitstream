using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-04: a user who has never confirmed a TOTP code sees a QR code on login instead of a
/// bare code prompt, and their first valid code both confirms enrollment and signs them in —
/// the flow that replaces "read the secret off a console log" for every account after the
/// first one seeded by <c>DevelopmentBootstrapper</c>.
/// </summary>
public sealed class TwoFactorEnrollmentTests
{
    [Fact]
    public async Task First_login_returns_a_QR_code_and_the_first_valid_code_confirms_enrollment()
    {
        await using var factory = new TwoFactorEnrollmentApiFactory();
        const string email = "enrollment-target@example.com";
        byte[] rawSecret;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var totpService = scope.ServiceProvider.GetRequiredService<ITotpService>();
            var totpProtector = scope.ServiceProvider.GetRequiredService<ITotpSecretProtector>();

            rawSecret = totpService.GenerateSecret();

            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000020");
            var seededUser = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email, totpConfirmed: false);
            seededUser.TotpSecret = await totpProtector.ProtectAsync(rawSecret);
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.True(loginResponse.StatusCode == HttpStatusCode.OK, $"Expected OK, got {loginResponse.StatusCode}: {loginBody}");

        var challenge = JsonSerializer.Deserialize<LoginChallengeResponse>(loginBody, JsonSerializerOptions.Web);
        Assert.NotNull(challenge);
        Assert.StartsWith("data:image/png;base64,", challenge!.QrCodeDataUri, StringComparison.Ordinal);

        string code;

        await using (var scope = factory.CreateAsyncScope())
        {
            var totpService = scope.ServiceProvider.GetRequiredService<ITotpService>();
            code = totpService.GenerateCurrentCode(rawSecret);
        }

        using var verifyResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login/verify", UriKind.Relative),
            new TwoFactorVerifyRequest(challenge.ChallengeToken, code));
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);

        await using var assertScope = factory.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var user = await assertDb.Users.SingleAsync(u => u.Email == email);
        Assert.NotNull(user.TotpConfirmedAt);
    }

    [Fact]
    public async Task Login_stops_returning_a_QR_code_once_enrollment_is_confirmed()
    {
        await using var factory = new TwoFactorEnrollmentApiFactory();
        const string email = "already-enrolled@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Beta", "L00000021");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email, totpConfirmed: true);
        }

        using var client = factory.CreateClient();

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));
        var loginBody = await loginResponse.Content.ReadAsStringAsync();
        Assert.True(loginResponse.StatusCode == HttpStatusCode.OK, $"Expected OK, got {loginResponse.StatusCode}: {loginBody}");

        var challenge = JsonSerializer.Deserialize<LoginChallengeResponse>(loginBody, JsonSerializerOptions.Web);
        Assert.Null(challenge!.QrCodeDataUri);
    }
}

/// <summary>
/// Distinct from <see cref="IdentityApiFactory"/> only in that it configures
/// <c>Secrets:TotpEncryptionKey</c>: these tests are the ones that actually round-trip a secret
/// through <see cref="ITotpSecretProtector"/>, which every other Totp-channel test avoids by
/// seeding an already-enrolled user (see <see cref="IdentitySeeder.AddUserAsync"/>).
/// </summary>
internal sealed class TwoFactorEnrollmentApiFactory : WebApplicationFactory<WebHostEntryPoint>
{
    private static readonly string ValidTotpEncryptionKey = Convert.ToBase64String(Convert.FromHexString(new string('b', 64)));

    private readonly string _databaseName = $"bitstream-2fa-enrollment-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("IdentityTests");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:FailFastOnSchemaMismatch"] = "false",
                ["WorkingCalendar:TimeZoneId"] = "UTC",
                ["Integration:OutboxDispatcher:Enabled"] = "false",
                ["Secrets:TotpEncryptionKey"] = ValidTotpEncryptionKey
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveEntityFrameworkCoreServices();
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();
}
