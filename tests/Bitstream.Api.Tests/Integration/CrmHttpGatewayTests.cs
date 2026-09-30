using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Xml.Linq;
using Bitstream.Api.Tests.Identity;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Infrastructure.Integration.Crm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// Direction A over the wire (TR-INT-15 to TR-INT-21): the SOAP Business Partner creation
/// (INT-CRM-01), the idempotency header and bearer credential on the JSON operations, and the
/// business-rejection/technical-failure split TR-INT-19/-20 require.
/// <see cref="CrmClosureEndToEndTests"/> exercises the same operations through
/// <see cref="FakeCrmGateway"/> instead, so the full activation flow does not depend on a real
/// HTTP round trip; this file is what actually proves <see cref="CrmHttpGateway"/>'s HTTP
/// behaviour, against a fake <see cref="HttpMessageHandler"/> rather than a real socket.
/// </summary>
public sealed class CrmHttpGatewayTests
{
    private const string BusinessPartnerEndpoint = "http://sap.example.com:5040/integration/sap";

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Respond(request);
        }
    }

    private static CrmHttpGateway CreateGateway(RecordingHandler handler, bool configureBusinessPartner = true, string? messageLogDirectory = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.com/") };
        var secretResolver = new FakeSecretResolver().Set("CrmClientSecret", "test-token");
        var options = Options.Create(new CrmOptions
        {
            CredentialSecretName = "CrmClientSecret",
            BusinessPartner = new CrmBusinessPartnerOptions
            {
                Endpoint = configureBusinessPartner ? new Uri(BusinessPartnerEndpoint) : null,
                MessageLogDirectory = messageLogDirectory
            }
        });

        return new CrmHttpGateway(client, options, secretResolver, NullLogger<CrmHttpGateway>.Instance);
    }

    private static CreateCrmCustomerCommand CustomerCommand() =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "ISP_1", DateTimeOffset.UtcNow),
            "ISP_1", "Alpha", "L12345678A", "Contact", "contact@example.com", "+355 672017664", "https://maps.example.com/?q=41.3275,19.8187");

    private static CreateActivationTicketCommand TicketCommand() =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "ISP_1", DateTimeOffset.UtcNow),
            "ISP_1", "CUST-1", "BP-1", "REQUEST_FOR_ACTIVATION", "BITSTREAM_STD", 12,
            "41.3275,19.8187", 41.3275m, 19.8187m, null);

    private static HttpResponseMessage SoapResponse(string responseCode, string contextStrings, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(
                "<?xml version='1.0' encoding='UTF-8'?><S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
                "<S:Body><ns2:executeOperationResponse xmlns:ns2=\"http://workflow.pi.altima.hr/\"><operationExecutionResponse>" +
                $"<responseCode>{responseCode}</responseCode><context>{contextStrings}</context>" +
                "</operationExecutionResponse></ns2:executeOperationResponse></S:Body></S:Envelope>",
                Encoding.UTF8,
                "text/xml")
        };

    [Fact]
    public async Task CreateCustomerAsync_posts_the_CRM_BP_CREATE_envelope_and_returns_BP_NO()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse("0", "<string name=\"RETURN_CODE\">9</string><string name=\"BP_NO\">1102017112</string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("1102017112", result.Value!.BusinessPartner);
        Assert.Equal("1102017112", result.Value.CrmCustomerId);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal(new Uri(BusinessPartnerEndpoint), handler.LastRequest.RequestUri);

        var envelope = XDocument.Parse(handler.LastBody!);
        Assert.Equal("CRM_BP_CREATE", envelope.Descendants("code").Single().Value);

        var context = envelope.Descendants("context").Single();
        Assert.Equal("requestContext", context.Attribute("name")!.Value);

        var values = context.Elements("string").ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal("https://maps.example.com/?q=41.3275,19.8187", values["GEOLOCATION"]);
        Assert.Equal("contact@example.com", values["EMAIL"]);
        Assert.Equal("L12345678A", values["IDNUMBER"]);
        Assert.Equal("ISP_1", values["NAME"]);
        Assert.Equal(string.Empty, values["SHORT_ADDR"]);
        Assert.Equal("O000", values["CUSTOMERTYPE"]);
        Assert.Equal("2", values["BP_CAT"]);
        Assert.Equal("O100", values["PARTNERTYPE"]);
        Assert.Equal("672017664", values["MOBILE"]);
    }

    [Fact]
    public async Task The_message_log_file_records_the_exact_request_XML_and_the_response()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bitstream-crm-bp-" + Guid.NewGuid().ToString("N"));
        const string mapsLink = "https://www.google.com/maps/place/41.3275,19.8187/@41.3275,19.8187,17z?entry=ttu&g_ep=abc";

        try
        {
            var handler = new RecordingHandler
            {
                Respond = _ => SoapResponse("0", "<string name=\"BP_NO\">1102017112</string>")
            };
            var gateway = CreateGateway(handler, messageLogDirectory: directory);

            await gateway.CreateCustomerAsync(CustomerCommand() with { Geolocation = mapsLink });

            var logFile = Assert.Single(Directory.GetFiles(directory, "crm-bp-soap-*.log"));
            var logText = await File.ReadAllTextAsync(logFile);

            // The request exactly as sent is in the file, and the link round-trips through XML.
            Assert.Contains(handler.LastBody!, logText, StringComparison.Ordinal);
            var geolocation = XDocument.Parse(handler.LastBody!).Descendants("string")
                .Single(e => e.Attribute("name")!.Value == "GEOLOCATION").Value;
            Assert.Equal(mapsLink, geolocation);

            Assert.Contains("RESPONSE HTTP 200", logText, StringComparison.Ordinal);
            Assert.Contains("1102017112", logText, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_non_zero_responseCode_is_a_business_rejection_not_a_retryable_failure()
    {
        // TR-INT-19: business rejections are never retried.
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse("1", "<string name=\"RETURN_CODE\">4</string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Contains("RETURN_CODE=4", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_responseCode_without_BP_NO_is_not_a_success()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse("0", "<string name=\"RETURN_CODE\">9</string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_SOAP_fault_is_a_retryable_technical_failure()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(
                    "<S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body><S:Fault>" +
                    "<faultcode>S:Server</faultcode><faultstring>Backend unavailable</faultstring></S:Fault></S:Body></S:Envelope>",
                    Encoding.UTF8,
                    "text/xml")
            }
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Contains("Backend unavailable", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_Business_Partner_endpoint_is_a_retryable_technical_failure()
    {
        var handler = new RecordingHandler();
        var gateway = CreateGateway(handler, configureBusinessPartner: false);

        var result = await gateway.CreateCustomerAsync(CustomerCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
        Assert.Null(handler.LastRequest);
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

    [Fact]
    public async Task CreateActivationTicketAsync_sends_the_idempotency_key_and_bearer_credential()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { crmTicketId = "TKT-1" })
            }
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("TKT-1", result.Value!.CrmTicketId);

        Assert.Equal("ISP_1", handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", handler.LastRequest.Headers.Authorization.Parameter);
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

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

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

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }
}
