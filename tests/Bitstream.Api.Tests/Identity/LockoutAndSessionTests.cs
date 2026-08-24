using System.Net;
using System.Net.Http.Json;
using Bitstream.Application.Identity.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-06 (lockout at 5 failed attempts) and TR-SEC-07 (session lifetime), proven through the
/// real HTTP pipeline. Session idle/absolute timeout is now ASP.NET Core's own cookie
/// authentication (<c>ConfigureApplicationCookie</c>, <c>Program.cs</c>) — Microsoft's own,
/// already-tested code, not re-tested here (the same reason the deleted <c>TotpServiceTests</c>/
/// <c>AesGcmTotpSecretProtectorTests</c> aren't replaced by anything: the code they tested no
/// longer exists in this app).
/// </summary>
public sealed class LockoutAndSessionTests
{
    [Fact]
    public async Task Account_locks_automatically_after_5_consecutive_failed_attempts()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "lockout-target@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000010");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
        }

        using var client = factory.CreateClient();

        // TR-SEC-06: the first 4 wrong attempts are ordinary invalid-credentials failures.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new LoginRequest(email, "definitely-the-wrong-password"));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // The 5th reaches the threshold and locks the account within the same response.
        using (var fifthAttempt = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, "definitely-the-wrong-password")))
        {
            Assert.Equal(HttpStatusCode.Locked, fifthAttempt.StatusCode);
        }

        // TR-SEC-12: locked and denied even with the correct password, without a 6th failure being recorded.
        using (var afterLock = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText)))
        {
            Assert.Equal(HttpStatusCode.Locked, afterLock.StatusCode);
        }

        await using var assertScope = factory.CreateAsyncScope();
        var userManager = assertScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);

        Assert.True(await userManager.IsLockedOutAsync(user));
        Assert.Equal(5, await userManager.GetAccessFailedCountAsync(user));

        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.Contains(assertDb.AuditLog, entry => entry.ActionCode == "Security.Account.AutoLocked");
    }

    [Fact]
    public async Task Correct_password_before_the_threshold_still_succeeds_and_resets_the_counter()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "recovers-before-lockout@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000011");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var loaded = await userManager.FindByIdAsync(user.Id.ToString());
            Assert.NotNull(loaded);

            for (var i = 0; i < 3; i++)
            {
                await userManager.AccessFailedAsync(loaded);
            }
        }

        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = await response.Content.ReadFromJsonAsync<TwoFactorRequiredResponse>();
        Assert.NotNull(challenge);
        Assert.Equal("Totp", challenge.Channel);

        await using var assertScope = factory.CreateAsyncScope();
        var assertUserManager = assertScope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var assertUser = await assertUserManager.FindByEmailAsync(email);
        Assert.NotNull(assertUser);

        Assert.Equal(0, await assertUserManager.GetAccessFailedCountAsync(assertUser));
        Assert.False(await assertUserManager.IsLockedOutAsync(assertUser));
    }

    [Fact]
    public async Task A_fresh_session_is_accepted_and_reports_the_caller()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "fresh-session@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000014");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
        }

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(me);
        Assert.Equal(email, me.Email);
        Assert.Equal("IspUser", me.Role);
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_it_can_no_longer_authenticate()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "logout-target@example.com";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000015");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
        }

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using (var beforeLogout = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);
        }

        using (var logout = await client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        // TR-SEC-07: invalidated immediately — the same cookie must not work again.
        using var afterLogout = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
