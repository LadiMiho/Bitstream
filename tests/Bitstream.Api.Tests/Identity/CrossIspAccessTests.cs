using System.Net;
using System.Net.Http.Json;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// TR-SEC-19: "Any attempt by an ISP user to access a record belonging to another ISP must
/// return a not-found response and must be logged as a security event." Proven through the real
/// HTTP pipeline — real session cookie authentication, real authorization, real
/// <c>AdministrationService</c> — not a unit test of the decision in isolation.
/// </summary>
public sealed class CrossIspAccessTests
{
    [Fact]
    public async Task An_ISP_user_reading_another_ISPs_record_gets_404_not_403()
    {
        await using var factory = new IdentityApiFactory();
        string ownIspUserToken;
        long otherIspId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var ownIsp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000001");
            var otherIsp = await IdentitySeeder.AddIspAsync(db, "Someone Else's ISP", "L00000002");
            var user = await IdentitySeeder.AddUserAsync(db, role, ownIsp.IspId, "own-isp-user@example.com");
            ownIspUserToken = await IdentitySeeder.AddSessionAsync(db, user.Id);
            otherIspId = otherIsp.IspId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={ownIspUserToken}");

        using var response = await client.GetAsync(new Uri($"/api/v1/isps/{otherIspId}", UriKind.Relative));

        // The requirement's exact wording: not-found, not forbidden. A 403 here would confirm
        // to the caller that ispId refers to a real record they simply may not see — 404 does not.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_cross_ISP_attempt_is_logged_as_a_security_event()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long otherIspId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var ownIsp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000003");
            var otherIsp = await IdentitySeeder.AddIspAsync(db, "Someone Else's ISP", "L00000004");
            var user = await IdentitySeeder.AddUserAsync(db, role, ownIsp.IspId, "own-isp-user-2@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);
            otherIspId = otherIsp.IspId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri($"/api/v1/isps/{otherIspId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var assertScope = factory.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        Assert.Contains(
            assertDb.AuditLog,
            entry => entry.ActionCode == "Security.AccessDenied.CrossIsp" &&
                     entry.EntityType == "Isp" &&
                     entry.EntityId == otherIspId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_ISP_user_reading_their_own_ISP_succeeds()
    {
        // The negative case (above) only means something next to this positive one: the
        // endpoint is not simply broken for everyone.
        await using var factory = new IdentityApiFactory();
        string token;
        long ownIspId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser");
            var ownIsp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000005");
            var user = await IdentitySeeder.AddUserAsync(db, role, ownIsp.IspId, "own-isp-user-3@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);
            ownIspId = ownIsp.IspId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri($"/api/v1/isps/{ownIspId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("Own ISP", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task An_Administrator_can_read_any_ISP()
    {
        // isp.read.all is what makes the difference — proving the not-found rule is about
        // ownership, not a blanket restriction on the endpoint.
        await using var factory = new IdentityApiFactory();
        string token;
        long otherIspId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "isp.read.all");
            var admin = await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, "admin@example.com");
            var otherIsp = await IdentitySeeder.AddIspAsync(db, "Some ISP", "L00000006");
            token = await IdentitySeeder.AddSessionAsync(db, admin.Id);
            otherIspId = otherIsp.IspId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri($"/api/v1/isps/{otherIspId}", UriKind.Relative));

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

        using var response = await client.GetAsync(new Uri($"/api/v1/isps/{ispId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
