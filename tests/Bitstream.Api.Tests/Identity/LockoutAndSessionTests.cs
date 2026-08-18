using System.Net;
using System.Net.Http.Json;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>TR-SEC-06 (lockout at 5 failed attempts) and TR-SEC-07 (session expiry), proven through the real HTTP pipeline.</summary>
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
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var user = await assertDb.Users.SingleAsync(u => u.Email == email);

        Assert.Equal(UserStatus.Locked, user.Status);
        Assert.Equal(5, user.FailedLoginCount);
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
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email, failedLoginCount: 3);
        }

        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new LoginRequest(email, TestPassword.PlainText));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var challenge = await response.Content.ReadFromJsonAsync<LoginChallengeResponse>();
        Assert.NotNull(challenge);
        Assert.False(string.IsNullOrWhiteSpace(challenge.ChallengeToken));

        await using var assertScope = factory.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var user = await assertDb.Users.SingleAsync(u => u.Email == email);

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Equal(UserStatus.Active, user.Status);
    }

    [Fact]
    public async Task A_session_idle_beyond_the_configured_timeout_is_rejected()
    {
        // TR-SEC-07: 30 minutes idle by default. LastActivityAt an hour ago, well past it —
        // ExpiresAt (the separate absolute cap) is left far in the future, so this specifically
        // isolates the idle rule from the absolute one.
        await using var factory = new IdentityApiFactory();
        string token;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000012");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "idle-session@example.com");

            var now = DateTimeOffset.UtcNow;
            token = await IdentitySeeder.AddSessionAsync(
                db, user.UserId,
                issuedAt: now.AddHours(-1),
                expiresAt: now.AddHours(11),
                lastActivityAt: now.AddHours(-1));
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_session_past_its_absolute_timeout_is_rejected_even_if_recently_active()
    {
        // TR-SEC-07: whichever limit is reached first. LastActivityAt is a minute ago (well
        // inside the idle window) but ExpiresAt has already passed.
        await using var factory = new IdentityApiFactory();
        string token;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000013");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "absolute-timeout@example.com");

            var now = DateTimeOffset.UtcNow;
            token = await IdentitySeeder.AddSessionAsync(
                db, user.UserId,
                issuedAt: now.AddHours(-13),
                expiresAt: now.AddMinutes(-60),
                lastActivityAt: now.AddMinutes(-1));
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_fresh_session_is_accepted_and_reports_the_caller()
    {
        await using var factory = new IdentityApiFactory();
        string token;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000014");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "fresh-session@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.UserId);
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(me);
        Assert.Equal("fresh-session@example.com", me.Email);
        Assert.Equal("IspUser", me.Role);
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_it_can_no_longer_authenticate()
    {
        await using var factory = new IdentityApiFactory();
        string token;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000015");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "logout-target@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.UserId);
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using (var beforeLogout = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, beforeLogout.StatusCode);
        }

        using (var logout = await client.PostAsync(new Uri("/api/v1/auth/logout", UriKind.Relative), content: null))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        }

        // TR-SEC-07: invalidated immediately — the same token must not work again.
        using var afterLogout = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }
}
