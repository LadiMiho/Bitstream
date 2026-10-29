using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Api.Tests.Integration;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Services.Integration;
using Bitstream.Domain.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.PostActivation;

/// <summary>
/// Post-activation requests end to end: the portal's form and submit, then the API host's outbox
/// dispatcher sending the BITSTREAM_TO_CRM workflow (INT-CRM-11) to a fake CRM.
/// </summary>
public sealed class PostActivationRequestTests
{
    private const string IspUserEmail = "tring-user@example.com";
    private const string OtherIspUserEmail = "abc-user@example.com";
    private const string AdminEmail = "pa-admin@example.com";
    private const string ViewerEmail = "pa-viewer@example.com";

    private sealed record Seeded(long Level1Id, long Level2Id, long Level3Id, long InactiveLevel3Id);

    private static async Task<Seeded> SeedAsync(CrmApiFactory factory)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "ticket.create", "ticket.read.own");
        var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "ticket.create", "ticket.read.all", "activation.read.all");
        var viewerRole = await IdentitySeeder.AddRoleAsync(db, "Viewer", "ticket.read.own");

        var tring = await IdentitySeeder.AddIspAsync(db, "Tring", "L00000301", "TRING");
        var abc = await IdentitySeeder.AddIspAsync(db, "Abc", "L00000302", "ABC");
        await IdentitySeeder.AddUserAsync(db, ispRole, tring.IspId, IspUserEmail);
        await IdentitySeeder.AddUserAsync(db, ispRole, abc.IspId, OtherIspUserEmail);
        await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, AdminEmail);
        await IdentitySeeder.AddUserAsync(db, viewerRole, tring.IspId, ViewerEmail);

        foreach (var phone in new[] { "Tring_768", "TRING_1", "TRINGX_1", "ABC_1", "NOPE_1", "TRING024", "TRINGGPON024", "0672005050" })
        {
            db.BiPhoneNumbers.Add(new BiPhoneNumber { Phonenumber = phone });
        }

        var now = DateTimeOffset.UtcNow;
        var level1 = new SupportClassification { Code = "40", Name = "BITSTREAM COMPLAINTS", Level = 1, DisplayOrder = 10, CreatedAt = now };
        var level2 = new SupportClassification { Code = "400144", Name = "NETWORK", Level = 2, Parent = level1, DisplayOrder = 10, CreatedAt = now };
        var level3 = new SupportClassification { Code = "4001440010", Name = "Cannot access to the internet", Level = 3, Parent = level2, DisplayOrder = 10, CreatedAt = now };
        var inactive = new SupportClassification { Code = "4001440011", Name = "Speed Problem", Level = 3, Parent = level2, DisplayOrder = 20, IsActive = false, CreatedAt = now };
        db.SupportClassifications.AddRange(level1, level2, level3, inactive);
        await db.SaveChangesAsync();

        return new Seeded(level1.ClassificationId, level2.ClassificationId, level3.ClassificationId, inactive.ClassificationId);
    }

    private static async Task<HttpClient> SignInAsync(PortalApiFactory portal, string email)
    {
        var client = portal.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, portal.Services, email);
        return client;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, string phone, long classificationId, string? note = null) =>
        client.PostAsJsonAsync(new Uri("/PostActivation", UriKind.Relative), new CreatePostActivationRequestHttpRequest(phone, classificationId, note));

    private static async Task<PostActivationRequest> ReadAsync(CrmApiFactory factory, string phone)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        return await db.PostActivationRequests.AsNoTracking().SingleAsync(r => r.Phonenumber == phone);
    }

    private static async Task DispatchAsync(CrmApiFactory factory)
    {
        await using var scope = factory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync();
    }

    [Fact]
    public async Task An_ISP_user_is_offered_only_their_own_phone_numbers_and_an_admin_all_of_them()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        await SeedAsync(factory);

        using var ispClient = await SignInAsync(portal, IspUserEmail);
        var ispDrawer = await ispClient.GetStringAsync(new Uri("/PostActivation/NewDrawer", UriKind.Relative));

        Assert.Contains("value=\"Tring_768\"", ispDrawer, StringComparison.Ordinal);
        Assert.Contains("value=\"TRING_1\"", ispDrawer, StringComparison.Ordinal);
        Assert.Contains("value=\"TRING024\"", ispDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("TRINGX_1", ispDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("TRINGGPON024", ispDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC_1", ispDrawer, StringComparison.Ordinal);
        // Only active classifications reach the linked dropdowns.
        Assert.Contains("4001440010", ispDrawer, StringComparison.Ordinal);
        Assert.DoesNotContain("4001440011", ispDrawer, StringComparison.Ordinal);

        using var adminClient = await SignInAsync(portal, AdminEmail);
        var adminDrawer = await adminClient.GetStringAsync(new Uri("/PostActivation/NewDrawer", UriKind.Relative));

        foreach (var phone in new[] { "Tring_768", "TRING_1", "TRINGX_1", "ABC_1", "NOPE_1" })
        {
            Assert.Contains($"value=\"{phone}\"", adminDrawer, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_phone_number_without_an_underscore_belongs_to_the_ISP_whose_ticket_code_starts_it()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory);

        using var ispClient = await SignInAsync(portal, IspUserEmail);
        using var response = await SubmitAsync(ispClient, "TRING024", seeded.Level3Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = await ReadAsync(factory, "TRING024");
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.Equal("TRING", (await db.Isps.AsNoTracking().SingleAsync(i => i.IspId == saved.IspId)).TicketCode);
    }

    [Fact]
    public async Task Invalid_submissions_are_rejected_next_to_the_field_and_nothing_is_queued()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory);

        using var ispClient = await SignInAsync(portal, IspUserEmail);
        using var adminClient = await SignInAsync(portal, AdminEmail);

        var cases = new (HttpClient Client, string Phone, long ClassificationId, string? Note, string Field)[]
        {
            (ispClient, "ABC_1", seeded.Level3Id, null, "phonenumber"),          // another ISP's number
            (ispClient, "UNKNOWN_9", seeded.Level3Id, null, "phonenumber"),      // not in BI's table
            (adminClient, "NOPE_1", seeded.Level3Id, null, "phonenumber"),       // no ISP has ticket code NOPE
            (adminClient, "TRINGGPON024", seeded.Level3Id, null, "phonenumber"), // no ISP has ticket code TRINGGPON
            (adminClient, "0672005050", seeded.Level3Id, null, "phonenumber"),   // no ticket code at all
            (ispClient, "Tring_768", seeded.Level2Id, null, "classificationId"), // not a level-3 item
            (ispClient, "Tring_768", seeded.InactiveLevel3Id, null, "classificationId"),
            (ispClient, "Tring_768", seeded.Level3Id, new string('x', 4001), "note")
        };

        foreach (var (client, phone, classificationId, note, field) in cases)
        {
            using var response = await SubmitAsync(client, phone, classificationId, note);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.Empty(db.PostActivationRequests);
        Assert.Empty(db.IntegrationMessages);
    }

    [Fact]
    public async Task Without_ticket_create_the_form_and_the_submit_are_forbidden()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory);

        using var client = await SignInAsync(portal, ViewerEmail);
        using var response = await SubmitAsync(client, "Tring_768", seeded.Level3Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_request_is_queued_sent_to_CRM_and_shows_the_ticket_number_only_to_its_own_ISP()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory);

        using var ispClient = await SignInAsync(portal, IspUserEmail);
        using var response = await SubmitAsync(ispClient, "Tring_768", seeded.Level3Id, "  Line down since morning  ");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var saved = await ReadAsync(factory, "Tring_768");
        Assert.Equal(PostActivationRequestStatus.PendingCrmSync, saved.Status);
        Assert.Equal("40", saved.Classification1Code);
        Assert.Equal("NETWORK", saved.Classification2Name);
        Assert.Equal("4001440010", saved.Classification3Code);
        Assert.Equal("Line down since morning", saved.Note);

        await DispatchAsync(factory);

        var call = Assert.Single(factory.CrmGateway.CreatePhoneTicketCalls);
        Assert.Equal("Tring_768", call.Phonenumber);
        Assert.Equal("4001440010", call.Classification);
        Assert.Equal("Line down since morning", call.Note);

        var sent = await ReadAsync(factory, "Tring_768");
        Assert.Equal(PostActivationRequestStatus.Submitted, sent.Status);
        Assert.NotNull(sent.CrmTicketId);

        var ownList = await ispClient.GetFromJsonAsync<PostActivationRequestListResponse>(new Uri("/PostActivation/Search", UriKind.Relative));
        var ownRow = Assert.Single(ownList!.Items);
        Assert.Equal(sent.PublicId, ownRow.PublicId);
        Assert.Equal(sent.CrmTicketId, ownRow.CrmTicketId);
        Assert.Equal("Submitted to CRM", ownRow.StatusLabel);

        using var otherClient = await SignInAsync(portal, OtherIspUserEmail);
        var otherList = await otherClient.GetFromJsonAsync<PostActivationRequestListResponse>(new Uri("/PostActivation/Search", UriKind.Relative));
        Assert.Empty(otherList!.Items);

        using var otherView = await otherClient.GetAsync(new Uri($"/PostActivation/Requests/{sent.PublicId}/ViewDrawer", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, otherView.StatusCode);

        var view = await ispClient.GetStringAsync(new Uri($"/PostActivation/Requests/{sent.PublicId}/ViewDrawer", UriKind.Relative));
        Assert.Contains("Cannot access to the internet", view, StringComparison.Ordinal);
        Assert.Contains("CRM answered", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CRM_answering_AlreadyExists_or_refusing_fails_the_request_with_its_message()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory);
        using var adminClient = await SignInAsync(portal, AdminEmail);

        using (var response = await SubmitAsync(adminClient, "TRING_1", seeded.Level3Id))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        factory.CrmGateway.NextCreatePhoneTicketResult = IntegrationResult<CreatePhoneTicketResult>.Success(new CreatePhoneTicketResult(
            "8009521751", "Ticket: 8009521751  already exist with status E0007", AlreadyExists: true,
            new Dictionary<string, string>
            {
                ["EvServiceTicketId"] = "8009521751",
                ["EvMessage"] = "Ticket: 8009521751  already exist with status E0007",
                ["EvAlreadyExists"] = "X"
            }));
        await DispatchAsync(factory);

        var existing = await ReadAsync(factory, "TRING_1");
        Assert.Equal(PostActivationRequestStatus.Failed, existing.Status);
        Assert.Null(existing.CrmTicketId);
        Assert.Equal("Ticket: 8009521751  already exist with status E0007", existing.CrmMessage);

        // CRM's answer is kept and shown as JSON in the drawer.
        var drawer = await adminClient.GetStringAsync(new Uri($"/PostActivation/Requests/{existing.PublicId}/ViewDrawer", UriKind.Relative));
        Assert.Contains("CRM response", drawer, StringComparison.Ordinal);
        Assert.Contains("&quot;EvAlreadyExists&quot;: &quot;X&quot;", drawer, StringComparison.Ordinal);

        using (var response = await SubmitAsync(adminClient, "ABC_1", seeded.Level3Id))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        factory.CrmGateway.NextCreatePhoneTicketResult = IntegrationResult<CreatePhoneTicketResult>.BusinessRejection("1", "Phone number not found");
        await DispatchAsync(factory);

        var refused = await ReadAsync(factory, "ABC_1");
        Assert.Equal(PostActivationRequestStatus.Failed, refused.Status);
        Assert.Contains("Phone number not found", refused.CrmMessage, StringComparison.Ordinal);
    }
}
