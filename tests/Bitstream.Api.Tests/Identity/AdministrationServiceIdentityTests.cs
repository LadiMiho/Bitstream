using System.Net;
using System.Net.Http.Json;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Proves <c>UserManager&lt;User&gt;</c> is genuinely wired in, not just compiling: a user
/// created through <c>POST /api/v1/users</c> (which now calls <c>UserManager.CreateAsync</c>,
/// not a hand-written repository) ends up with a real Argon2id hash that
/// <c>POST /api/v1/auth/login</c> (via <c>UserManager.CheckPasswordAsync</c>) can verify.
/// </summary>
public sealed class AdministrationServiceIdentityTests
{
    [Fact]
    public async Task A_user_created_through_the_API_can_immediately_authenticate_with_the_password_it_was_given()
    {
        await using var factory = new TwoFactorEnrollmentApiFactory();
        const string adminEmail = "identity-admin@example.com";
        const string newUserEmail = "identity-new-user@example.com";
        const string newUserPassword = "Correct-Horse-Battery-Staple-9";
        string adminToken;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "user.create");
            var admin = await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, adminEmail);
            adminToken = await IdentitySeeder.AddSessionAsync(db, admin.UserId);

            // The new user's own role also needs to exist — CreateUserAsync validates it.
            await IdentitySeeder.AddRoleAsync(db, "IspUser");
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={adminToken}");

        using var createResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/users", UriKind.Relative),
            new CreateUserHttpRequest(null, "New Employee", newUserEmail, "+355691234567", "IspUser", newUserPassword));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var created = await createResponse.Content.ReadFromJsonAsync<UserResponse>();
        Assert.NotNull(created);

        await using (var assertScope = factory.CreateAsyncScope())
        {
            var db = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var storedUser = await db.Users.SingleAsync(u => u.Email == newUserEmail);

            // Argon2id, not a placeholder — proves UserManager.CreateAsync actually hashed it via
            // Argon2IdentityPasswordHasher rather than leaving the required-but-empty default.
            Assert.StartsWith("$argon2id$", storedUser.PasswordHash, StringComparison.Ordinal);
        }

        using var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(newUserEmail, newUserPassword));

        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }
}
