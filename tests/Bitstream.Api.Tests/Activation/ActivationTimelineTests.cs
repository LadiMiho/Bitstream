using System.Net;
using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Activation;

/// <summary>
/// The activation request View drawer's timeline: what <see cref="ActivationTimeline"/> makes of
/// the stored messages and audit rows, and the drawer itself through the portal pipeline.
/// </summary>
public sealed class ActivationTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    private static ActivationRequest Request() => new()
    {
        RequestId = 7,
        PublicId = "TRING_001",
        PackageCode = "BITSTREAM_STD",
        LocationRaw = "41.3275,19.8187",
        Classification = "REQUEST_FOR_ACTIVATION",
        ContractDurationMonths = 12,
        CrmTicketId = "8009521719",
        OperatorComment = "No sync on the ONT"
    };

    private static IntegrationMessage Message(
        IntegrationDirection direction, string interfaceCode, string? messageType, string payload, IntegrationMessageStatus status,
        DateTimeOffset at, string? response = null, string? error = null, string relatedPublicId = "TRING_001") => new()
        {
            Direction = direction,
            TargetSystem = TargetSystem.Crm,
            InterfaceCode = interfaceCode,
            MessageType = messageType,
            Payload = payload,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Status = status,
            CorrelationId = "c",
            CreatedAt = at,
            ResponsePayload = response,
            LastError = error,
            RelatedPublicId = relatedPublicId
        };

    [Fact]
    public void Builds_one_ordered_story_from_CRM_messages_and_portal_actions()
    {
        var history = new ActivationHistory(
            [
                Message(IntegrationDirection.Inbound, "INT-CRM-EVENT", "SALES_ORDER_OPENED",
                    """{"EventId":"e2","Payload":{"SalesOrderId":"SO-1"}}""", IntegrationMessageStatus.Succeeded, T0.AddMinutes(30), "{}"),
                Message(IntegrationDirection.Outbound, "INT-CRM-01", "CREATE_CUSTOMER", "{}", IntegrationMessageStatus.Succeeded,
                    T0.AddMinutes(1), """{"CrmCustomerId":"C1","BusinessPartner":"1102017112"}"""),
                Message(IntegrationDirection.Outbound, "INT-CRM-02", "CREATE_TICKET", """{"OfferCode":"5100020013"}""",
                    IntegrationMessageStatus.Succeeded, T0.AddMinutes(2), """{"CrmTicketId":"8009521719"}"""),
                Message(IntegrationDirection.Inbound, "INT-CRM-EVENT", "LINE_ACTIVATED", """{"Payload":{}}""",
                    IntegrationMessageStatus.DeadLettered, T0.AddMinutes(20), error: "409 Invalid state transition: wrong order"),
                Message(IntegrationDirection.Inbound, "INT-CRM-EVENT", "LINE_AVAILABLE", """{"Payload":{}}""",
                    IntegrationMessageStatus.Succeeded, T0.AddMinutes(40), """{"discarded":true,"reason":"stale"}"""),
                Message(IntegrationDirection.Outbound, "INT-CRM-10", "OPERATOR_CONFIRMATION", """{"Confirmed":"N","Comment":"No sync on the ONT"}""",
                    IntegrationMessageStatus.DeadLettered, T0.AddMinutes(60), error: "INT-CRM-10 contract not yet defined")
            ],
            [
                new AuditLog { ActionCode = "ActivationRequest.Submitted", EntityType = "ActivationRequest", EntityId = "7", ActorUserId = 5, Timestamp = T0, CorrelationId = "c" },
                new AuditLog
                {
                    ActionCode = "ActivationRequest.OperatorConfirmationRecorded", EntityType = "ActivationRequest", EntityId = "7", ActorUserId = 5,
                    Timestamp = T0.AddMinutes(59), NewValue = """{"status":"WaitingForServiceDesk","working":false}""", CorrelationId = "c"
                }
            ],
            new Dictionary<long, string> { [5] = "Ana Operator" });

        var timeline = ActivationTimeline.Build(Request(), history);

        Assert.Equal(
            [
                "Request submitted", "Create Business Partner in CRM", "Create activation ticket in CRM", "CRM: line activated",
                "CRM: sales order opened", "CRM: line available", "Operator confirmation", "Send operator confirmation to CRM"
            ],
            timeline.Select(entry => entry.Title));

        Assert.Equal("Ana Operator", timeline[0].Actor);
        Assert.Equal(ActivationTimelineKind.Portal, timeline[0].Kind);
        Assert.Equal("Business Partner 1102017112", timeline[1].Summary);
        Assert.Contains("CRM ticket 8009521719", timeline[2].Summary, StringComparison.Ordinal);
        Assert.Equal(ActivationTimelineOutcome.Rejected, timeline[3].Outcome);
        Assert.Contains("409", timeline[3].Error, StringComparison.Ordinal);
        Assert.Equal("Sales order SO-1", timeline[4].Summary);
        Assert.Equal(ActivationTimelineOutcome.Discarded, timeline[5].Outcome);
        Assert.Contains("Line working: No", timeline[6].Summary, StringComparison.Ordinal);
        Assert.Contains("No sync on the ONT", timeline[6].Summary, StringComparison.Ordinal);
        Assert.Equal(ActivationTimelineOutcome.Failed, timeline[7].Outcome);
        Assert.Equal(ActivationTimelineKind.Outbound, timeline[7].Kind);
    }

    [Fact]
    public void Unknown_audit_actions_are_left_out()
    {
        var history = new ActivationHistory(
            [],
            [new AuditLog { ActionCode = "ActivationRequest.Something", EntityType = "ActivationRequest", EntityId = "7", ActorUserId = 5, Timestamp = T0, CorrelationId = "c" }],
            new Dictionary<long, string>());

        Assert.Empty(ActivationTimeline.Build(Request(), history));
    }

    [Theory]
    [InlineData("activation.read.all", true)]
    [InlineData("activation.read.own", false)]
    public async Task The_View_drawer_shows_the_timeline_and_raw_data_only_to_activation_read_all(string permission, bool expectRaw)
    {
        await using var factory = new IdentityApiFactory();
        var email = $"timeline-{expectRaw}@example.com";
        string publicId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, expectRaw ? "Administrator" : "IspUser", permission);
            var isp = await IdentitySeeder.AddIspAsync(db, "Timeline ISP", "L00000120");
            await IdentitySeeder.AddUserAsync(db, role, expectRaw ? null : isp.IspId, email);

            var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, "TIME_001", ActivationRequestStatus.AwaitingGisVerification);
            request.CrmTicketId = "8009521719";
            publicId = request.PublicId;

            db.IntegrationMessages.Add(Message(IntegrationDirection.Outbound, "INT-CRM-01", "CREATE_CUSTOMER", """{"RequestPublicId":"TIME_001"}""",
                IntegrationMessageStatus.Succeeded, T0, """{"CrmCustomerId":"C1","BusinessPartner":"1102017112"}""", relatedPublicId: "TIME_001"));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        await IdentitySeeder.AuthenticateAsync(client, factory.Services, email);

        using var response = await client.GetAsync(new Uri($"/ActivationRequests/{publicId}/ViewDrawer", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Communication with CRM", html, StringComparison.Ordinal);
        Assert.Contains("Create Business Partner in CRM", html, StringComparison.Ordinal);
        Assert.Contains("Business Partner 1102017112", html, StringComparison.Ordinal);
        Assert.Equal(expectRaw, html.Contains("data-role=\"timeline-raw\"", StringComparison.Ordinal));
    }
}
