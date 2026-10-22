using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Contracts;
using Bitstream.Api.Tests.Activation;
using Bitstream.Api.Tests.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// The CRM inbound API's access rule (X-Api-Key), lookup by CRM's ticket number, and the GIS line
/// check reported by CRM (LINE_AVAILABLE / NO_LINE) and LINE_ACTIVATED — through the real pipeline of
/// <c>Bitstream.Api</c>.
/// </summary>
public sealed class CrmInboundApiTests
{
    private const string CrmTicketNumber = "8009521719";

    private static async Task<long> SeedAwaitingGisAsync(CrmApiFactory factory, string publicId)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        var isp = await IdentitySeeder.AddIspAsync(db, "Tring", "L00000950", "TRING");
        var request = await ActivationSeeder.AddRequestAsync(db, isp.IspId, publicId, ActivationRequestStatus.AwaitingGisVerification);
        request.CrmTicketId = CrmTicketNumber;
        await db.SaveChangesAsync();

        return request.RequestId;
    }

    private static TicketEventRequest Event(string eventType, string? identifier, string? reason = null) =>
        new(Guid.NewGuid().ToString(), eventType, identifier, CrmTicketNumber, DateTimeOffset.UtcNow,
            new TicketEventPayload(null, null, null, null, null, null, null, null, null, null, reason));

    private static async Task<(ActivationRequestStatus Status, string? Reason)> ReadAsync(CrmApiFactory factory, long requestId)
    {
        await using var scope = factory.CreateAsyncScope();
        var request = await scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().ActivationRequests.FindAsync(requestId);

        return (request!.Status, request.StatusReason);
    }

    [Fact]
    public async Task A_call_without_the_API_key_is_refused_with_401_and_changes_nothing()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClientWithoutApiKey();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event("LINE_AVAILABLE", "TRING_001"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingGisVerification, (await ReadAsync(factory, requestId)).Status);
    }

    [Fact]
    public async Task A_call_with_a_wrong_API_key_is_refused_with_401()
    {
        await using var factory = new CrmApiFactory();
        await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClientWithoutApiKey();
        client.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event("LINE_AVAILABLE", "TRING_001"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LINE_AVAILABLE_addressed_by_CRM_ticket_number_moves_the_request_to_LineAvailable()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClient();

        // No identifier in the body: the route carries CRM's own ticket number.
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{CrmTicketNumber}/events", UriKind.Relative), Event("LINE_AVAILABLE", identifier: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.LineAvailable, (await ReadAsync(factory, requestId)).Status);
    }

    [Fact]
    public async Task NO_LINE_without_a_reason_is_rejected_with_422()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event("NO_LINE", "TRING_001"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingGisVerification, (await ReadAsync(factory, requestId)).Status);
    }

    [Fact]
    public async Task NO_LINE_with_a_reason_moves_the_request_to_RejectedNoLine_and_records_the_reason()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event("NO_LINE", "TRING_001", "No fibre in this street"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (status, reason) = await ReadAsync(factory, requestId);
        Assert.Equal(ActivationRequestStatus.RejectedNoLine, status);
        Assert.Equal("No fibre in this street", reason);
    }

    [Fact]
    public async Task A_step_out_of_order_is_rejected_with_409()
    {
        await using var factory = new CrmApiFactory();
        await SeedAwaitingGisAsync(factory, "TRING_001");
        using var client = factory.CreateClient();

        // Still AwaitingGisVerification: the line can't be activated before the line check and sales order.
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event("LINE_ACTIVATED", "TRING_001"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The refusal is recorded on the stored event, for the request's timeline and the dead letter list.
        await using var scope = factory.CreateAsyncScope();
        var stored = Assert.Single(scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().IntegrationMessages,
            m => m.Direction == IntegrationDirection.Inbound);
        Assert.Equal(IntegrationMessageStatus.DeadLettered, stored.Status);
        Assert.StartsWith("409 Invalid state transition", stored.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LINE_ACTIVATED_moves_the_request_to_AwaitingOperatorConfirmation()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{CrmTicketNumber}/events", UriKind.Relative), Event("LINE_ACTIVATED", identifier: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingOperatorConfirmation, (await ReadAsync(factory, requestId)).Status);
    }

    [Theory]
    [InlineData("PROVISIONING_STARTED")]
    [InlineData("TECHNICALLY_COMPLETED")]
    public async Task The_retired_provisioning_events_are_rejected_with_422(string eventType)
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING_001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/TRING_001/events", UriKind.Relative), Event(eventType, "TRING_001"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.SalesOrderOpened, (await ReadAsync(factory, requestId)).Status);
    }

    private static async Task SetStatusAsync(CrmApiFactory factory, long requestId, ActivationRequestStatus status)
    {
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        var request = await db.ActivationRequests.FindAsync(requestId);
        request!.Status = status;
        await db.SaveChangesAsync();
    }
    [Fact]
    public async Task A_body_that_is_not_valid_JSON_is_answered_400_with_where_it_breaks_not_500()
    {
        await using var factory = new CrmApiFactory();
        using var client = factory.CreateClient();

        // Exactly what was sent from the CRM side: unquoted text as the payload.
        const string body = """
            {
              "eventId": "8009521803",
              "eventType": "LINE_AVAILABLE",
              "occurredAt": "2026-10-19T12:58:36.000+02:00",
              "payload": {per alben}
            }
            """;
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/api/v1/tickets/8009521803/events", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadAsStringAsync();
        Assert.Contains("The request body could not be read", problem, StringComparison.Ordinal);
        Assert.Contains("$.payload", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_at_all_is_answered_400()
    {
        await using var factory = new CrmApiFactory();
        using var client = factory.CreateClient();

        using var content = new StringContent("eventType=LINE_AVAILABLE", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/api/v1/tickets/8009521803/events", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    private const string SalesOrderTicket = "8009529999";

    private static TicketEventRequest SalesOrderOpened(string? salesOrderId) =>
        new(Guid.NewGuid().ToString(), "SALES_ORDER_OPENED", null, null, DateTimeOffset.UtcNow,
            new TicketEventPayload(null, null, null, null, null, null, null, null, salesOrderId, null));

    private static async Task<string?> ReadSalesOrderAsync(CrmApiFactory factory, long requestId)
    {
        await using var scope = factory.CreateAsyncScope();
        var request = await scope.ServiceProvider.GetRequiredService<BitstreamDbContext>().ActivationRequests.FindAsync(requestId);
        return request!.SalesOrderId;
    }

    [Fact]
    public async Task SALES_ORDER_OPENED_on_CRMs_new_ticket_is_matched_by_the_request_ID_in_salesOrderId()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.LineAvailable);
        using var client = factory.CreateClient();

        // The route carries the new ticket CRM opened for the sales order, unknown to the portal;
        // payload.salesOrderId carries our request ID.
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{SalesOrderTicket}/events", UriKind.Relative), SalesOrderOpened("TRING001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.SalesOrderOpened, (await ReadAsync(factory, requestId)).Status);
        Assert.Equal(SalesOrderTicket, await ReadSalesOrderAsync(factory, requestId));

        // Later events addressed to that new ticket reach the same request.
        using var activated = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{SalesOrderTicket}/events", UriKind.Relative),
            Event("LINE_ACTIVATED", identifier: null) with { OccurredAt = DateTimeOffset.UtcNow.AddSeconds(5) });

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingOperatorConfirmation, (await ReadAsync(factory, requestId)).Status);
    }

    [Fact]
    public async Task SALES_ORDER_OPENED_naming_an_unknown_request_is_404_and_says_salesOrderId_must_be_the_request_ID()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.LineAvailable);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{SalesOrderTicket}/events", UriKind.Relative), SalesOrderOpened("TRING999"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("payload.salesOrderId 'TRING999'", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ActivationRequestStatus.LineAvailable, (await ReadAsync(factory, requestId)).Status);
    }
    private static TicketEventRequest LineActivated(string? salesOrderId) =>
        new(Guid.NewGuid().ToString(), "LINE_ACTIVATED", null, null, DateTimeOffset.UtcNow,
            new TicketEventPayload(null, null, null, null, null, null, null, null, salesOrderId, null));

    [Fact]
    public async Task LINE_ACTIVATED_on_a_ticket_the_portal_does_not_know_is_matched_by_the_request_ID_in_salesOrderId()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/8009539999/events", UriKind.Relative), LineActivated("TRING001"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingOperatorConfirmation, (await ReadAsync(factory, requestId)).Status);

        // The stored message is linked to the request, so it shows on the request's timeline.
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
        Assert.Contains(db.IntegrationMessages, m => m.MessageType == "LINE_ACTIVATED" && m.RelatedPublicId == "TRING001");
    }

    [Fact]
    public async Task LINE_ACTIVATED_without_salesOrderId_still_works_by_the_identifier_in_the_URL()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/tickets/{CrmTicketNumber}/events", UriKind.Relative), LineActivated(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ActivationRequestStatus.AwaitingOperatorConfirmation, (await ReadAsync(factory, requestId)).Status);
    }

    [Fact]
    public async Task LINE_ACTIVATED_naming_an_unknown_request_on_an_unknown_ticket_is_404_naming_salesOrderId()
    {
        await using var factory = new CrmApiFactory();
        var requestId = await SeedAwaitingGisAsync(factory, "TRING001");
        await SetStatusAsync(factory, requestId, ActivationRequestStatus.SalesOrderOpened);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/tickets/8009539999/events", UriKind.Relative), LineActivated("TRING999"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("payload.salesOrderId 'TRING999'", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(ActivationRequestStatus.SalesOrderOpened, (await ReadAsync(factory, requestId)).Status);
    }
}
