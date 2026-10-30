using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Api.Tests.Integration;
using Bitstream.Application.Services.Integration;
using Bitstream.Domain.Entities;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Notifications;

/// <summary>
/// In-app notifications: created when a request's status changes, for the audiences the
/// Notification settings switch on (never for the user who made the change), then shown by the
/// header bell and the Notifications page, and marked read when opened.
/// </summary>
public sealed class NotificationTests
{
    private const string IspUserEmail = "ntf-isp@example.com";
    private const string IspColleagueEmail = "ntf-colleague@example.com";
    private const string OtherIspUserEmail = "ntf-other@example.com";
    private const string ServiceDeskEmail = "ntf-desk@example.com";
    private const string AdminEmail = "ntf-admin@example.com";

    private sealed record Seeded(long Level3Id);

    private static async Task<Seeded> SeedAsync(CrmApiFactory factory, params (string EventCode, string Audience)[] rules)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "ticket.create", "ticket.read.own", "activation.read.own");
        var deskRole = await IdentitySeeder.AddRoleAsync(db, NotificationAudiences.ServiceDeskRoleName, "ticket.read.all", "activation.read.all");
        var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "ticket.create", "ticket.read.all", "activation.read.all", "notification.manage");

        var tring = await IdentitySeeder.AddIspAsync(db, "Tring", "L00000401", "TRING");
        var abc = await IdentitySeeder.AddIspAsync(db, "Abc", "L00000402", "ABC");
        await IdentitySeeder.AddUserAsync(db, ispRole, tring.IspId, IspUserEmail);
        await IdentitySeeder.AddUserAsync(db, ispRole, tring.IspId, IspColleagueEmail);
        await IdentitySeeder.AddUserAsync(db, ispRole, abc.IspId, OtherIspUserEmail);
        await IdentitySeeder.AddUserAsync(db, deskRole, ispId: null, ServiceDeskEmail);
        await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, AdminEmail);

        db.BiPhoneNumbers.Add(new BiPhoneNumber { Phonenumber = "TRING024" });

        var now = DateTimeOffset.UtcNow;
        var level1 = new SupportClassification { Code = "40", Name = "BITSTREAM COMPLAINTS", Level = 1, DisplayOrder = 10, CreatedAt = now };
        var level2 = new SupportClassification { Code = "400144", Name = "NETWORK", Level = 2, Parent = level1, DisplayOrder = 10, CreatedAt = now };
        var level3 = new SupportClassification { Code = "4001440010", Name = "Cannot access to the internet", Level = 3, Parent = level2, DisplayOrder = 10, CreatedAt = now };
        db.SupportClassifications.AddRange(level1, level2, level3);

        foreach (var (eventCode, audience) in rules)
        {
            db.NotificationRules.Add(new NotificationRule { EventCode = eventCode, Audience = audience, IsEnabled = true });
        }

        await db.SaveChangesAsync();
        return new Seeded(level3.ClassificationId);
    }

    private static async Task<HttpClient> SignInAsync(PortalApiFactory portal, string email)
    {
        var client = portal.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await IdentitySeeder.AuthenticateAsync(client, portal.Services, email);
        return client;
    }

    private static async Task<List<UserNotification>> NotificationsOfAsync(CrmApiFactory factory, string email)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return await db.UserNotifications.AsNoTracking().Where(n => n.UserId == userId).OrderBy(n => n.NotificationId).ToListAsync();
    }

    private static async Task<string> SubmitAndDispatchAsync(CrmApiFactory factory, PortalApiFactory portal, Seeded seeded)
    {
        using var client = await SignInAsync(portal, IspUserEmail);
        using var response = await client.PostAsJsonAsync(
            new Uri("/PostActivation", UriKind.Relative), new CreatePostActivationRequestHttpRequest("TRING024", seeded.Level3Id, null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync();

        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        return (await db.PostActivationRequests.AsNoTracking().SingleAsync(r => r.Phonenumber == "TRING024")).PublicId;
    }

    [Fact]
    public async Task A_status_change_notifies_the_requests_ISP_users_but_not_other_ISPs_or_the_user_who_made_it()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory,
            ("PostActivationRequest:PendingCrmSync", NotificationAudiences.IspUsers),
            ("PostActivationRequest:InProcess", NotificationAudiences.IspUsers));

        var publicId = await SubmitAndDispatchAsync(factory, portal, seeded);

        // The creator is not told about their own new request; their colleague is.
        var own = await NotificationsOfAsync(factory, IspUserEmail);
        var colleague = await NotificationsOfAsync(factory, IspColleagueEmail);
        Assert.Equal([PostActivationRequestStatus.InProcess], own.Select(n => n.Status));
        Assert.Equal([PostActivationRequestStatus.PendingCrmSync, PostActivationRequestStatus.InProcess], colleague.Select(n => n.Status));

        var inProcess = own.Single();
        Assert.Equal(NotificationEntityTypes.PostActivationRequest, inProcess.EntityType);
        Assert.Equal(publicId, inProcess.EntityPublicId);
        Assert.Equal(PostActivationRequestStatus.PendingCrmSync, inProcess.PreviousStatus);
        Assert.Null(inProcess.ReadAt);

        Assert.Empty(await NotificationsOfAsync(factory, OtherIspUserEmail));
        Assert.Empty(await NotificationsOfAsync(factory, ServiceDeskEmail));
    }

    [Fact]
    public async Task Only_the_switched_on_audiences_are_notified_and_CRMs_reason_is_kept()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory, ("PostActivationRequest:TechnicallyCompleted", NotificationAudiences.ServiceDesk));

        var publicId = await SubmitAndDispatchAsync(factory, portal, seeded);
        Assert.Empty(await NotificationsOfAsync(factory, IspColleagueEmail));

        string ticketNo;
        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            ticketNo = (await db.PostActivationRequests.AsNoTracking().SingleAsync(r => r.PublicId == publicId)).CrmTicketId!;
        }

        using var crm = factory.CreateClient();
        using var closed = await crm.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{ticketNo}/events", UriKind.Relative),
            new { eventId = Guid.NewGuid().ToString(), eventType = "TECHNICALLY_COMPLETED", occurredAt = DateTimeOffset.UtcNow, payload = new { reason = "Fibre spliced" } });
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var desk = Assert.Single(await NotificationsOfAsync(factory, ServiceDeskEmail));
        Assert.Equal(PostActivationRequestStatus.TechnicallyCompleted, desk.Status);
        Assert.Equal("Fibre spliced", desk.Detail);
        Assert.Empty(await NotificationsOfAsync(factory, IspColleagueEmail));
    }

    [Fact]
    public async Task The_bell_lists_the_users_unread_notifications_and_opening_one_marks_it_read_and_shows_the_request()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        var seeded = await SeedAsync(factory,
            ("PostActivationRequest:PendingCrmSync", NotificationAudiences.IspUsers),
            ("PostActivationRequest:InProcess", NotificationAudiences.IspUsers));
        var publicId = await SubmitAndDispatchAsync(factory, portal, seeded);

        using var client = await SignInAsync(portal, IspColleagueEmail);

        var page = await client.GetStringAsync(new Uri("/PostActivation", UriKind.Relative));
        Assert.Contains("data-role=\"notification-bell\"", page, StringComparison.Ordinal);

        var summary = await client.GetFromJsonAsync<NotificationSummaryResponse>(new Uri("/Notifications/Summary", UriKind.Relative));
        Assert.Equal(2, summary!.UnreadCount);
        Assert.Equal($"Post-activation request {publicId}", summary.Items[0].Title);
        Assert.Equal("Status changed from Pending CRM sync to In process", summary.Items[0].Message);
        Assert.Equal("New request: Pending CRM sync", summary.Items[1].Message);

        // Opening marks it read and goes to the request, with its drawer opened.
        using (var open = await client.GetAsync(new Uri(summary.Items[0].Url, UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
            Assert.Equal($"/PostActivation?open={publicId}", open.Headers.Location!.OriginalString);
        }

        var unread = await client.GetFromJsonAsync<NotificationListResponse>(new Uri("/Notifications/Search?filter=unread", UriKind.Relative));
        Assert.Equal("New request: Pending CRM sync", Assert.Single(unread!.Items).Message);
        Assert.Equal(1, unread.UnreadCount);

        var all = await client.GetFromJsonAsync<NotificationListResponse>(new Uri("/Notifications/Search", UriKind.Relative));
        Assert.Equal(2, all!.TotalCount);
        Assert.True(all.Items[0].Read);

        // Someone else's notification cannot be opened.
        using var other = await SignInAsync(portal, OtherIspUserEmail);
        using (var foreign = await other.GetAsync(new Uri(summary.Items[1].Url, UriKind.Relative)))
        {
            Assert.Equal("/Notifications", foreign.Headers.Location!.OriginalString);
        }

        using (var readAll = await client.PostAsync(new Uri("/Notifications/ReadAll", UriKind.Relative), null))
        {
            Assert.Equal(HttpStatusCode.OK, readAll.StatusCode);
        }

        var after = await client.GetFromJsonAsync<NotificationSummaryResponse>(new Uri("/Notifications/Summary", UriKind.Relative));
        Assert.Equal(0, after!.UnreadCount);
        Assert.Empty(after.Items);
    }

    [Fact]
    public async Task Only_notification_manage_reaches_the_settings_and_saving_switches_rules_on_and_off()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        await SeedAsync(factory, ("ActivationRequest:LineAvailable", NotificationAudiences.IspUsers));

        using var ispClient = await SignInAsync(portal, IspUserEmail);
        using (var refused = await ispClient.GetAsync(new Uri("/AccessManagement/NotificationSettings", UriKind.Relative)))
        {
            Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        }

        using var admin = await SignInAsync(portal, AdminEmail);
        var settings = await admin.GetStringAsync(new Uri("/AccessManagement/NotificationSettings", UriKind.Relative));
        Assert.Contains("data-event-code=\"ActivationRequest:LineAvailable\" data-audience=\"IspUsers\" checked=\"checked\"", settings, StringComparison.Ordinal);
        Assert.Contains("Technically completed", settings, StringComparison.Ordinal);

        using (var saved = await admin.PostAsJsonAsync(
            new Uri("/AccessManagement/NotificationSettings", UriKind.Relative),
            new SaveNotificationSettingsRequest([
                new NotificationRuleRequest("ActivationRequest:WaitingForServiceDesk", NotificationAudiences.ServiceDesk),
                new NotificationRuleRequest("Unknown:Event", NotificationAudiences.IspUsers)
            ])))
        {
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        }

        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var enabled = await db.NotificationRules.AsNoTracking().Where(r => r.IsEnabled).Select(r => r.EventCode + "/" + r.Audience).ToListAsync();
        Assert.Equal(["ActivationRequest:WaitingForServiceDesk/ServiceDesk"], enabled);
        Assert.True(await db.AuditLog.AnyAsync(a => a.ActionCode == "NotificationSettings.Updated"));
    }

    [Fact]
    public async Task An_activation_request_status_change_from_CRM_notifies_its_ISP_users_with_the_reason()
    {
        await using var factory = new CrmApiFactory();
        await SeedAsync(factory, ("ActivationRequest:RejectedNoLine", NotificationAudiences.IspUsers));

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var tringId = await db.Isps.Where(i => i.TicketCode == "TRING").Select(i => i.IspId).SingleAsync();
            await Bitstream.Api.Tests.Activation.ActivationSeeder.AddRequestAsync(db, tringId, "TRING009", Bitstream.Domain.Enums.ActivationRequestStatus.AwaitingGisVerification);
        }

        using var crm = factory.CreateClient();
        using var response = await crm.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING009/events", UriKind.Relative),
            new { eventId = Guid.NewGuid().ToString(), eventType = "NO_LINE", occurredAt = DateTimeOffset.UtcNow, payload = new { reason = "No fibre in this street" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var email in new[] { IspUserEmail, IspColleagueEmail })
        {
            var notification = Assert.Single(await NotificationsOfAsync(factory, email));
            Assert.Equal(NotificationEntityTypes.ActivationRequest, notification.EntityType);
            Assert.Equal("TRING009", notification.EntityPublicId);
            Assert.Equal("AwaitingGisVerification", notification.PreviousStatus);
            Assert.Equal("RejectedNoLine", notification.Status);
            Assert.Equal("No fibre in this street", notification.Detail);
        }

        Assert.Empty(await NotificationsOfAsync(factory, OtherIspUserEmail));
    }
}
