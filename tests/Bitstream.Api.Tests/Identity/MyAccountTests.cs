using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bitstream.Application.Identity.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>The user menu's My account and Change password drawers: the signed-in user's own details and changing their own password.</summary>
public sealed class MyAccountTests
{
    private const string Email = "me@tring.al";
    private const string NewPassword = "Brand-New-Passw0rd-2026!";

    private static async Task<HttpClient> SignedInIspUserAsync(IdentityApiFactory factory)
    {
        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own");
            var isp = await IdentitySeeder.AddIspAsync(db, "Tring", "L00000501", "TRING");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, Email);
        }

        var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, Email);
        return client;
    }

    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string current, string next, string? confirm = null) =>
        client.PostAsJsonAsync(new Uri("/Account/Password", UriKind.Relative), new ChangeOwnPasswordHttpRequest(current, next, confirm ?? next));

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
    }

    [Fact]
    public async Task The_user_menu_opens_My_account_and_Change_password_in_separate_drawers()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInIspUserAsync(factory);

        var page = await client.GetStringAsync(new Uri("/ActivationRequests", UriKind.Relative));
        Assert.Contains("data-account-drawer=\"account\"", page, StringComparison.Ordinal);
        Assert.Contains("data-account-drawer=\"password\"", page, StringComparison.Ordinal);
        Assert.Contains("/js/account-menu.js", page, StringComparison.Ordinal);

        var account = await client.GetStringAsync(new Uri("/Account/ViewDrawer", UriKind.Relative));
        Assert.Contains(Email, account, StringComparison.Ordinal);
        Assert.Contains("355691234567", account, StringComparison.Ordinal);
        Assert.Contains("IspUser", account, StringComparison.Ordinal);
        Assert.Contains("Tring", account, StringComparison.Ordinal);
        Assert.DoesNotContain("account-password-form", account, StringComparison.Ordinal);

        var password = await client.GetStringAsync(new Uri("/Account/PasswordDrawer", UriKind.Relative));
        Assert.Contains("id=\"account-password-form\"", password, StringComparison.Ordinal);
        Assert.Contains("name=\"currentPassword\"", password, StringComparison.Ordinal);
        Assert.DoesNotContain("Account created", password, StringComparison.Ordinal);

        // There is no separate account page any more.
        using var oldPage = await client.GetAsync(new Uri("/Account", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, oldPage.StatusCode);
    }

    [Fact]
    public async Task A_wrong_current_password_a_mismatch_or_a_weak_password_is_refused_next_to_its_field()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInIspUserAsync(factory);

        using (var wrong = await ChangeAsync(client, "not-my-password", NewPassword))
        {
            Assert.Contains("currentPassword", (await ErrorsAsync(wrong)).Keys);
        }

        using (var mismatch = await ChangeAsync(client, TestPassword.PlainText, NewPassword, "Something-Else-2026!"))
        {
            Assert.Contains("confirmPassword", (await ErrorsAsync(mismatch)).Keys);
        }

        using (var weak = await ChangeAsync(client, TestPassword.PlainText, "short"))
        {
            Assert.Contains("newPassword", (await ErrorsAsync(weak)).Keys);
        }

        using (var same = await ChangeAsync(client, TestPassword.PlainText, TestPassword.PlainText))
        {
            Assert.Contains("newPassword", (await ErrorsAsync(same)).Keys);
        }

        await using var scope = factory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(Email);
        Assert.True(await userManager.CheckPasswordAsync(user!, TestPassword.PlainText));
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_session_signed_in_and_the_new_password_works()
    {
        await using var factory = new IdentityApiFactory();
        using var client = await SignedInIspUserAsync(factory);

        using (var changed = await ChangeAsync(client, TestPassword.PlainText, NewPassword))
        {
            Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        }

        // Still signed in on this client (the cookie was re-issued with the new security stamp).
        using (var stillIn = await client.GetAsync(new Uri("/Notifications/Summary", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
        }

        await using var scope = factory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await userManager.FindByEmailAsync(Email);
        Assert.True(await userManager.CheckPasswordAsync(user!, NewPassword));
        Assert.False(await userManager.CheckPasswordAsync(user!, TestPassword.PlainText));
        Assert.NotNull(user!.PasswordUpdatedAt);

        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(a => a.ActionCode == "User.OwnPasswordChanged"));
    }
}
