using System.Net;
using System.Net.Http.Json;
using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Infrastructure.Integration.Crm;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// Direction A over the wire (TR-INT-15 to TR-INT-21): the idempotency header, the bearer
/// credential, and the business-rejection/technical-failure split TR-INT-19/-20 require.
/// <see cref="CrmClosureEndToEndTests"/> exercises the same operations through
/// <see cref="FakeCrmGateway"/> instead, so the full activation flow does not depend on a real
/// HTTP round trip; this file is what actually proves <see cref="CrmHttpGateway"/>'s HTTP
/// behaviour, against a fake <see cref="HttpMessageHandler"/> rather than a real socket.
/// </summary>
public sealed class CrmHttpGatewayTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    private static CrmHttpGateway CreateGateway(RecordingHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.com/") };
        var secretResolver = new FakeSecretResolver().Set("CrmClientSecret", "test-token");
        var options = Options.Create(new CrmOptions { CredentialSecretName = "CrmClientSecret" });

        return new CrmHttpGateway(client, options, secretResolver);
    }

    private static CreateCrmCustomerCommand CustomerCommand() =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "ISP_1", DateTimeOffset.UtcNow),
            "ISP_1", "Alpha", "L1", "Contact", "contact@example.com", "+355691234567");

    [Fact]
    public async Task CreateCustomerAsync_sends_the_idempotency_key_and_bearer_credential()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { crmCustomerId = "CUST-1", businessPartner = "BP-1" })
            }
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("CUST-1", result.Value!.CrmCustomerId);
        Assert.Equal("BP-1", result.Value.BusinessPartner);

        Assert.Equal("ISP_1", handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task CreateActivationTicketAsync_succeeds_on_a_2xx_response()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { crmTicketId = "TKT-1" })
            }
        };
        var gateway = CreateGateway(handler);

        var command = new CreateActivationTicketCommand(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "ISP_1", DateTimeOffset.UtcNow),
            "ISP_1", "CUST-1", "BP-1", "REQUEST_FOR_ACTIVATION", "BITSTREAM_STD", 12,
            "41.3275,19.8187", 41.3275m, 19.8187m, null);

        var result = await gateway.CreateActivationTicketAsync(command);

        Assert.True(result.IsSuccess);
        Assert.Equal("TKT-1", result.Value!.CrmTicketId);
    }

    [Fact]
    public async Task A_400_response_is_a_business_rejection_not_a_retryable_failure()
    {
        // TR-INT-19: business rejections are never retried.
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { message = "Invalid NIPT" })
            }
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Equal("Invalid NIPT", result.ErrorMessage);
    }

    [Fact]
    public async Task A_500_response_is_a_retryable_technical_failure()
    {
        // TR-INT-04: transient/server failures are retried.
        var handler = new RecordingHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    [Fact]
    public async Task A_dropped_connection_is_a_retryable_technical_failure()
    {
        var handler = new RecordingHandler { Respond = _ => throw new HttpRequestException("Connection refused") };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }
}
