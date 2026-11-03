using System.Net;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-19: "Any attempt by an ISP user to access a record belonging to another ISP must
/// return a not-found response and must be logged as a security event." Proven through the real
/// HTTP pipeline — real cookie authentication, real authorization, real
/// <c>AdministrationService</c> — not a unit test of the decision in isolation.
/// </summary>
public sealed class CrossIspAccessTests
{
    /// <summary>An ISP user of "Own ISP" and one activation request in each of two ISPs.</summary>
    private static async Task<(string OwnRequest, string OtherRequest, long OtherRequestId, long OwnIspId)> SeedIspUserAsync(IdentityApiFactory factory, string email)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own", "user.update", "isp.update");
        var ownIsp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000001");
        var otherIsp = await IdentitySeeder.AddIspAsync(db, "Someone Else's ISP", "L00000002");
        await IdentitySeeder.AddUserAsync(db, role, ownIsp.IspId, email);

        var own = await Activation.ActivationSeeder.AddRequestAsync(db, ownIsp.IspId, "OWNISP001");
        var other = await Activation.ActivationSeeder.AddRequestAsync(db, otherIsp.IspId, "OTHER001");
        return (own.PublicId, other.PublicId, other.RequestId, ownIsp.IspId);
    }

    [Fact]
    public async Task An_ISP_user_reading_another_ISPs_record_gets_404_not_403()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "own-isp-user@example.com";
        var seeded = await SeedIspUserAsync(factory, email);

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri($"/ActivationRequests/{seeded.OtherRequest}/ViewDrawer", UriKind.Relative));

        // The requirement's exact wording: not-found, not forbidden. A 403 here would confirm
        // to the caller that the ID refers to a real record they simply may not see — 404 does not.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_cross_ISP_attempt_is_logged_as_a_security_event()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "own-isp-user-2@example.com";
        var seeded = await SeedIspUserAsync(factory, email);

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri($"/ActivationRequests/{seeded.OtherRequest}/ViewDrawer", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var assertScope = factory.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        Assert.Contains(
            assertDb.AuditLog,
            entry => entry.ActionCode == "Security.AccessDenied.CrossIsp" &&
                     entry.EntityType == "ActivationRequest" &&
                     entry.EntityId == seeded.OtherRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_ISP_user_reading_their_own_ISPs_record_succeeds()
    {
        // The negative case (above) only means something next to this positive one: the
        // endpoint is not simply broken for everyone.
        await using var factory = new IdentityApiFactory();
        const string email = "own-isp-user-3@example.com";
        var seeded = await SeedIspUserAsync(factory, email);

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri($"/ActivationRequests/{seeded.OwnRequest}/ViewDrawer", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(seeded.OwnRequest, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_ISP_user_is_refused_every_Access_Management_page_and_call_and_does_not_see_the_menu()
    {
        await using var factory = new IdentityApiFactory();
        const string email = "own-isp-user-4@example.com";
        var seeded = await SeedIspUserAsync(factory, email);

        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        // Even the ISP user's own ISP, and even with user.update / isp.update on the role.
        foreach (var path in new[]
                 {
                     "/AccessManagement", "/AccessManagement/Isps", $"/AccessManagement/Isps/{seeded.OwnIspId}", "/AccessManagement/Isps/Search",
                     "/AccessManagement/Users", "/AccessManagement/Users/Search", "/AccessManagement/AuditLog", "/AccessManagement/NotificationSettings"
                 })
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path} answered {(int)response.StatusCode}");
        }

        var page = await client.GetStringAsync(new Uri($"/ActivationRequests", UriKind.Relative));
        Assert.DoesNotContain("Access Management", page, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/Reporting\"", page, StringComparison.Ordinal);
        Assert.Contains("data-account-drawer=\"account\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Administrator_can_read_any_ISP()
    {
        // isp.read.all is what makes the difference — proving the not-found rule is about
        // ownership, not a blanket restriction on the endpoint.
        await using var factory = new IdentityApiFactory();
        const string email = "admin@example.com";
        long otherIspId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "isp.read.all");
            await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, email);
            var otherIsp = await IdentitySeeder.AddIspAsync(db, "Some ISP", "L00000006");
            otherIspId = otherIsp.IspId;
        }

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri($"/AccessManagement/Isps/{otherIspId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_rejected_with_401()
    {
        await using var factory = new IdentityApiFactory();
        long ispId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var isp = await IdentitySeeder.AddIspAsync(db, "Some ISP", "L00000007");
            ispId = isp.IspId;
        }

        using var client = factory.CreateClient();
        // No cookie set at all.

        using var response = await client.GetAsync(new Uri($"/AccessManagement/Isps/{ispId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
