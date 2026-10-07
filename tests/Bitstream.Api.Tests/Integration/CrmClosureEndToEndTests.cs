using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Contracts;
using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Services.Integration;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// End-to-end reproduction of TRD §7.3.3's closure example: an activation request goes from
/// submission to Completed through both CRM directions — Direction A (customer and ticket
/// creation, dispatched from the outbox to <see cref="FakeCrmGateway"/>) and Direction B (sales
/// order and line activated, delivered on the inbound event API) — with the GIS verification
/// admin screen in between and the operator's confirmation at the end.
/// <para>
/// Also proves the two mechanics Direction B is built on: a repeated eventId is a no-op
/// (TR-INT-25) and an event no later than the one already applied is discarded, not applied
/// (TR-INT-25, TR-PAS-17) — both through the real HTTP pipeline, not asserted against the
/// service in isolation.
/// </para>
/// </summary>
public sealed class CrmClosureEndToEndTests
{
    [Fact]
    public async Task Activation_request_reaches_Completed_through_both_CRM_directions()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        const string ispUserEmail = "closure-isp-user@example.com";
        const string adminEmail = "closure-admin@example.com";
        long ispId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.create", "activation.read.own", "activation.confirm");
            var adminRole = await IdentitySeeder.AddRoleAsync(db, "Administrator", "activation.gis.record", "activation.read.all");

            var isp = await IdentitySeeder.AddIspAsync(db, "Closure Example ISP", "L00000900");
            await IdentitySeeder.AddUserAsync(db, ispRole, isp.IspId, ispUserEmail);
            await IdentitySeeder.AddUserAsync(db, adminRole, ispId: null, adminEmail);
            await Bitstream.Api.Tests.Activation.ActivationSeeder.SeedCatalogueAsync(db);

            ispId = isp.IspId;
        }

        using var client = factory.CreateClient();
        using var portalClient = portal.CreateClient();

        // --- Submit (TR-ACT-06, TR-DAT-01) --------------------------------------------------
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, ispUserEmail);

        using var submitResponse = await portalClient.PostAsJsonAsync(
            new Uri("/ActivationRequests", UriKind.Relative),
            new SubmitActivationHttpRequest(ispId, "BITSTREAM_STD", "41.3275,19.8187", "REQUEST_FOR_ACTIVATION", 12, "Closure example"));

        Assert.Equal(HttpStatusCode.Created, submitResponse.StatusCode);
        var submitted = await submitResponse.Content.ReadFromJsonAsync<ActivationRequestResponse>();
        Assert.NotNull(submitted);
        Assert.Equal("PendingCrmSync", submitted!.Status);
        var publicId = submitted.PublicId;
        var requestId = submitted.RequestId;

        // --- Direction A: the outbox dispatcher drives INT-CRM-01 then INT-CRM-02 ------------
        await using (var scope = factory.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

            var firstCycle = await dispatcher.DispatchBatchAsync();
            Assert.Equal(1, firstCycle); // INT-CRM-01 only — INT-CRM-02 does not exist yet.

            // BP_NO is stored as soon as INT-CRM-01 returns it, before the ticket exists.
            await using (var checkScope = factory.CreateAsyncScope())
            {
                var db = checkScope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
                var afterCustomer = await db.ActivationRequests.FindAsync(requestId);

                Assert.Equal("BP-000001", afterCustomer!.Bp);
                Assert.Equal(ActivationRequestStatus.PendingCrmSync, afterCustomer.Status);
            }

            var secondCycle = await dispatcher.DispatchBatchAsync();
            Assert.Equal(1, secondCycle); // INT-CRM-02, enqueued by the first cycle's success.
        }

        Assert.Single(factory.CrmGateway.CreateCustomerCalls);
        // The form's location reaches the Business Partner call as GEOLOCATION.
        Assert.Equal("41.3275,19.8187", factory.CrmGateway.CreateCustomerCalls[0].Geolocation);
        Assert.Single(factory.CrmGateway.CreateActivationTicketCalls);
        // TR-INT-03/17: the ticket call carries the BP the customer call actually returned, not a placeholder.
        Assert.Equal("BP-000001", factory.CrmGateway.CreateActivationTicketCalls[0].BusinessPartner);
        // The package + duration code chosen at submission reaches the ticket call (CLASS_3).
        Assert.Equal("5100020013", factory.CrmGateway.CreateActivationTicketCalls[0].OfferCode);

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var request = await db.ActivationRequests.FindAsync(requestId);

            Assert.Equal(ActivationRequestStatus.AwaitingGisVerification, request!.Status);
            Assert.Equal("CRMCUST-000001", request.CrmCustomerId);
            Assert.Equal("BP-000001", request.Bp);
            Assert.Equal("CRMTKT-000001", request.CrmTicketId);
        }

        // --- GIS verification admin screen (TR-ACT-12 to TR-ACT-19) -------------------------
        using (var logoutResponse = await portalClient.PostAsync(new Uri("/Auth/Logout", UriKind.Relative), content: null))
        {
            logoutResponse.EnsureSuccessStatusCode();
        }
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, adminEmail);

        using var gisResponse = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/gis-outcome", UriKind.Relative),
            new GisOutcomeRequest(true, null));
        Assert.Equal(HttpStatusCode.NoContent, gisResponse.StatusCode);

        // --- Direction B: sales order, provisioning, completion (TRD 5.3) -------------------
        var t0 = DateTimeOffset.UtcNow;

        using var salesOrderResponse = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{publicId}/events", UriKind.Relative),
            SalesOrderEvent("evt-sales-order-1", publicId, t0.AddMinutes(1)));
        Assert.Equal(HttpStatusCode.OK, salesOrderResponse.StatusCode);
        var salesOrderAccepted = await salesOrderResponse.Content.ReadFromJsonAsync<TicketEventAccepted>();
        Assert.False(salesOrderAccepted!.Duplicate);

        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);

        // TR-INT-25: the same eventId again is a no-op, not a second application.
        using var duplicateResponse = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{publicId}/events", UriKind.Relative),
            SalesOrderEvent("evt-sales-order-1", publicId, t0.AddMinutes(1)));
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode);
        var duplicateAccepted = await duplicateResponse.Content.ReadFromJsonAsync<TicketEventAccepted>();
        Assert.True(duplicateAccepted!.Duplicate);
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);

        // TR-INT-25 / TR-PAS-17: a new eventId, but occurredAt no later than the last applied
        // event, is discarded — accepted (200), not applied.
        using var staleResponse = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{publicId}/events", UriKind.Relative),
            LineActivatedEvent("evt-line-activated-stale", publicId, t0.AddMinutes(1)));
        Assert.Equal(HttpStatusCode.OK, staleResponse.StatusCode);
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);

        using var lineActivatedResponse = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{publicId}/events", UriKind.Relative),
            LineActivatedEvent("evt-line-activated-1", publicId, t0.AddMinutes(2)));
        Assert.Equal(HttpStatusCode.OK, lineActivatedResponse.StatusCode);
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.AwaitingOperatorConfirmation);

        // --- Operator confirmation in the portal: the ISP user says the line works ----------
        using (var logoutResponse = await portalClient.PostAsync(new Uri("/Auth/Logout", UriKind.Relative), content: null))
        {
            logoutResponse.EnsureSuccessStatusCode();
        }
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, ispUserEmail);

        using var confirmResponse = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/operator-confirmation", UriKind.Relative),
            new OperatorConfirmationRequest(true, null));
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.Completed);

        // INT-CRM-10 carries the answer to CRM through the outbox.
        await using (var scope = factory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync();
        }

        var confirmation = Assert.Single(factory.CrmGateway.OperatorConfirmationCalls);
        Assert.Equal(publicId, confirmation.RequestPublicId);
        Assert.Equal("Y", confirmation.Confirmed);
    }

    [Fact]
    public async Task Operator_no_then_service_desk_fail_ends_in_ActivationFailed()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        const string ispUserEmail = "operator@example.com";
        const string serviceDeskEmail = "servicedesk@example.com";
        long requestId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

            var ispRole = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.read.own", "activation.confirm");
            var serviceDeskRole = await IdentitySeeder.AddRoleAsync(db, "ServiceDesk", "activation.read.all", "activation.servicedesk.decide");

            var isp = await IdentitySeeder.AddIspAsync(db, "Operator ISP", "L00000901", "OPER");
            await IdentitySeeder.AddUserAsync(db, ispRole, isp.IspId, ispUserEmail);
            await IdentitySeeder.AddUserAsync(db, serviceDeskRole, ispId: null, serviceDeskEmail);

            var request = await Bitstream.Api.Tests.Activation.ActivationSeeder.AddRequestAsync(
                db, isp.IspId, "OPER_001", ActivationRequestStatus.AwaitingOperatorConfirmation);
            requestId = request.RequestId;
        }

        using var portalClient = portal.CreateClient();
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, ispUserEmail);

        // No without a comment: a field error on "comment", nothing changes.
        using (var missingComment = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/operator-confirmation", UriKind.Relative),
            new OperatorConfirmationRequest(false, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingComment.StatusCode);
            Assert.Contains("\"comment\"", await missingComment.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.AwaitingOperatorConfirmation);

        using (var no = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/operator-confirmation", UriKind.Relative),
            new OperatorConfirmationRequest(false, "No sync on the ONT")))
        {
            Assert.Equal(HttpStatusCode.NoContent, no.StatusCode);
        }
        await AssertStatusAsync(factory, requestId, ActivationRequestStatus.WaitingForServiceDesk);

        // The ISP user cannot make the service desk's decision.
        using (var forbidden = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/service-desk-decision", UriKind.Relative),
            new ServiceDeskDecisionRequest(true, "x")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var logoutResponse = await portalClient.PostAsync(new Uri("/Auth/Logout", UriKind.Relative), content: null))
        {
            logoutResponse.EnsureSuccessStatusCode();
        }
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, serviceDeskEmail);

        using (var fail = await portalClient.PatchAsJsonAsync(
            new Uri($"/ActivationRequests/{requestId}/service-desk-decision", UriKind.Relative),
            new ServiceDeskDecisionRequest(false, "Fault in the street cabinet; cancelled")))
        {
            Assert.Equal(HttpStatusCode.NoContent, fail.StatusCode);
        }

        await using (var scope = factory.CreateAsyncScope())
        {
            var request = await scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().ActivationRequests.FindAsync(requestId);

            Assert.Equal(ActivationRequestStatus.ActivationFailed, request!.Status);
            Assert.False(request.OperatorConfirmed);
            Assert.Equal("No sync on the ONT", request.OperatorComment);
            Assert.False(request.ServiceDeskSucceeded);
            Assert.Equal("Fault in the street cabinet; cancelled", request.ServiceDeskComment);
        }
    }

    [Fact]
    public async Task An_inbound_event_for_an_unknown_identifier_is_rejected_with_404()
    {
        // No portal host here: nothing is submitted, so there is no request for the identifier
        // to resolve to — which is exactly what this asserts.
        await using var factory = new CrmApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/ISP_999999/events", UriKind.Relative),
            SalesOrderEvent("evt-unknown", "ISP_999999", DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_sales_order_event_out_of_sequence_is_rejected_with_409()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        const string email = "out-of-sequence@example.com";
        long ispId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.create");
            var isp = await IdentitySeeder.AddIspAsync(db, "Out Of Sequence ISP", "L00000901");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
            await Bitstream.Api.Tests.Activation.ActivationSeeder.SeedCatalogueAsync(db);
            ispId = isp.IspId;
        }

        using var client = factory.CreateClient();
        using var portalClient = portal.CreateClient();
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, email);

        using var submitResponse = await portalClient.PostAsJsonAsync(
            new Uri("/ActivationRequests", UriKind.Relative),
            new SubmitActivationHttpRequest(ispId, "BITSTREAM_STD", "41.3275,19.8187", "REQUEST_FOR_ACTIVATION", 12, null));
        var submitted = await submitResponse.Content.ReadFromJsonAsync<ActivationRequestResponse>();

        // Still Submitted/PendingCrmSync — SALES_ORDER_OPENED is only valid from LineAvailable.
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{submitted!.PublicId}/events", UriKind.Relative),
            SalesOrderEvent("evt-out-of-sequence", submitted.PublicId, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_business_rejection_from_CRM_dead_letters_immediately_and_marks_the_request_IntegrationFailed()
    {
        await using var factory = new CrmApiFactory();
        await using var portal = new PortalApiFactory(factory.DatabaseName, factory.CrmGateway);
        const string email = "rejected@example.com";
        long ispId;

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var role = await IdentitySeeder.AddRoleAsync(db, "IspUser", "activation.create");
            var isp = await IdentitySeeder.AddIspAsync(db, "Rejected ISP", "L00000902");
            await IdentitySeeder.AddUserAsync(db, role, isp.IspId, email);
            await Bitstream.Api.Tests.Activation.ActivationSeeder.SeedCatalogueAsync(db);
            ispId = isp.IspId;
        }

        using var client = factory.CreateClient();
        using var portalClient = portal.CreateClient();
        await IdentitySeeder.AuthenticateAsync(portalClient, portal.Services, email);

        using var submitResponse = await portalClient.PostAsJsonAsync(
            new Uri("/ActivationRequests", UriKind.Relative),
            new SubmitActivationHttpRequest(ispId, "BITSTREAM_STD", "41.3275,19.8187", "REQUEST_FOR_ACTIVATION", 12, null));
        var submitted = await submitResponse.Content.ReadFromJsonAsync<ActivationRequestResponse>();

        // TR-INT-19: a business rejection is never retried — it dead-letters on the first attempt.
        factory.CrmGateway.NextCreateCustomerResult = IntegrationResult<CreateCrmCustomerResult>.BusinessRejection("400", "Invalid NIPT");

        await using (var scope = factory.CreateAsyncScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
            await dispatcher.DispatchBatchAsync();
        }

        await AssertStatusAsync(factory, submitted!.RequestId, ActivationRequestStatus.IntegrationFailed);
    }

    [Fact]
    public async Task A_customer_message_queued_without_geolocation_takes_it_from_the_activation_request()
    {
        await using var factory = new CrmApiFactory();
        const string publicId = "ISP_700";

        await using (var scope = factory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var isp = await IdentitySeeder.AddIspAsync(db, "Old Payload ISP", "L00000903");
            await Bitstream.Api.Tests.Activation.ActivationSeeder.AddRequestAsync(db, isp.IspId, publicId, ActivationRequestStatus.PendingCrmSync);

            // The payload shape enqueued before Geolocation existed on the command.
            var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationOutbox>();
            var legacyPayload =
                "{\"Envelope\":{\"MessageId\":\"" + Guid.NewGuid() + "\",\"CorrelationId\":\"corr-old\",\"IdempotencyKey\":\"" + publicId + "\"," +
                "\"OccurredAt\":\"2026-09-01T00:00:00+00:00\"},\"RequestPublicId\":\"" + publicId + "\",\"IspName\":\"Old Payload ISP\"," +
                "\"IspNipt\":\"L00000903\",\"ContactPerson\":\"Contact\",\"ContactEmail\":\"old@example.com\",\"ContactMobile\":\"+355691234567\"}";

            await outbox.EnqueueOutboundAsync(
                TargetSystem.Crm, "INT-CRM-01", "CREATE_CUSTOMER", publicId, legacyPayload, "corr-old", publicId);
        }

        await using (var scope = factory.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync();
        }

        var call = Assert.Single(factory.CrmGateway.CreateCustomerCalls);
        Assert.Equal("41.3275,19.8187", call.Geolocation);
    }

    private static async Task AssertStatusAsync(CrmApiFactory factory, long requestId, ActivationRequestStatus expected)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var request = await db.ActivationRequests.FindAsync(requestId);

        Assert.Equal(expected, request!.Status);
    }

    private static TicketEventRequest SalesOrderEvent(string eventId, string identifier, DateTimeOffset occurredAt) =>
        new(eventId, "SALES_ORDER_OPENED", identifier, null, occurredAt,
            new TicketEventPayload(null, null, null, null, null, null, null, null, "SO-12345", "BP-000001"));

    private static TicketEventRequest LineActivatedEvent(string eventId, string identifier, DateTimeOffset occurredAt) =>
        new(eventId, "LINE_ACTIVATED", identifier, null, occurredAt,
            new TicketEventPayload(null, null, null, null, null, null, null, null, null, null));
}
