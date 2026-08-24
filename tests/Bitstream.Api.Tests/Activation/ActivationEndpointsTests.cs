using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// TRD 5 endpoints proven through the real HTTP pipeline — real session cookie authentication,
/// real RBAC, real <c>ActivationRequestService</c> — reusing <c>IdentityApiFactory</c> since it
/// hosts the whole <c>Program</c> pipeline, not just the identity endpoints.
/// </summary>
public sealed class ActivationEndpointsTests
{
    [Fact]
    public async Task Submit_without_the_permission_is_rejected_with_403()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long ispId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            // ServiceDesk is seeded with no activation.create permission.
            var role = await IdentitySeeder.AddRoleAsync(db, "ServiceDesk", "ticket.read.all");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000101");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "servicedesk@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);
            ispId = isp.IspId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/activation-requests", UriKind.Relative),
            new SubmitActivationHttpRequest(ispId, "BITSTREAM_STD", "41.3275,19.8187", "REQUEST_FOR_ACTIVATION", 12, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_ISP_user_reading_another_ISPs_activation_request_gets_404_not_403()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        string otherRequestPublicId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.create", "activation.read.own");
            var ownIsp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000102");
            var otherIsp = await IdentitySeeder.AddIspAsync(db, "Other ISP", "L00000103");
            var user = await IdentitySeeder.AddUserAsync(db, role, ownIsp.IspId, "own-user@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);

            var otherRequest = await ActivationSeeder.AddRequestAsync(db, otherIsp.IspId, "ISP_1001");
            otherRequestPublicId = otherRequest.PublicId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri($"/api/v1/activation-requests/{otherRequestPublicId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_ISP_user_reading_their_own_activation_request_succeeds()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        string publicId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own");
            var isp = await IdentitySeeder.AddIspAsync(db, "Own ISP", "L00000104");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "own-user-2@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);

            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "ISP_1002");
            publicId = request.PublicId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.GetAsync(new Uri($"/api/v1/activation-requests/{publicId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ActivationRequestResponse>();
        Assert.Equal(publicId, body!.PublicId);
    }

    [Fact]
    public async Task GisOutcome_without_the_permission_is_rejected_with_403()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long requestId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.create");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000105");
            var user = await IdentitySeeder.AddUserAsync(db, role, isp.IspId, "isp-user@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, user.Id);

            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "ISP_1003", ActivationRequestStatus.AwaitingGisVerification);
            requestId = request.RequestId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/activation-requests/{requestId}/gis-outcome", UriKind.Relative),
            new GisOutcomeRequest(true, null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GisOutcome_no_line_without_a_reason_is_rejected_with_a_validation_problem()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long requestId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.gis.record");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000106");
            var admin = await IdentitySeeder.AddUserAsync(db, role, ispId: null, "admin-2@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, admin.Id);

            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "ISP_1004", ActivationRequestStatus.AwaitingGisVerification);
            requestId = request.RequestId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/activation-requests/{requestId}/gis-outcome", UriKind.Relative),
            new GisOutcomeRequest(false, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GisOutcome_line_available_moves_the_request_to_LineAvailable_and_is_audited()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long requestId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.gis.record");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000107");
            var admin = await IdentitySeeder.AddUserAsync(db, role, ispId: null, "admin-3@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, admin.Id);

            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "ISP_1005", ActivationRequestStatus.AwaitingGisVerification);
            requestId = request.RequestId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/activation-requests/{requestId}/gis-outcome", UriKind.Relative),
            new GisOutcomeRequest(true, null));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var assertScope = factory.CreateAsyncScope();
        var assertDb = assertScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var persisted = await assertDb.ActivationRequests.FindAsync(requestId);

        Assert.Equal(ActivationRequestStatus.LineAvailable, persisted!.Status);
        Assert.Contains(assertDb.AuditLog, e => e.ActionCode == "ActivationRequest.GisOutcomeRecorded");
    }

    [Fact]
    public async Task GisOutcome_on_a_request_not_awaiting_verification_is_rejected_with_409()
    {
        await using var factory = new IdentityApiFactory();
        string token;
        long requestId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.gis.record");
            var isp = await IdentitySeeder.AddIspAsync(db, "Alpha", "L00000108");
            var admin = await IdentitySeeder.AddUserAsync(db, role, ispId: null, "admin-4@example.com");
            token = await IdentitySeeder.AddSessionAsync(db, admin.Id);

            // Still Submitted — GIS verification has not been reached yet.
            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "ISP_1006", ActivationRequestStatus.Submitted);
            requestId = request.RequestId;
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"bitstream_session={token}");

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/activation-requests/{requestId}/gis-outcome", UriKind.Relative),
            new GisOutcomeRequest(true, null));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
