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
/// Direction A over the wire (TR-INT-15 to TR-INT-21): the SOAP Business Partner and activation
/// ticket creation (INT-CRM-01/02), the idempotency header and bearer credential on the JSON
/// operations, and the
/// business-rejection/technical-failure split TR-INT-19/-20 require.
/// <see cref="CrmClosureEndToEndTests"/> exercises the same operations through
/// <see cref="FakeCrmGateway"/> instead, so the full activation flow does not depend on a real
/// HTTP round trip; this file is what actually proves <see cref="CrmHttpGateway"/>'s HTTP
/// behaviour, against a fake <see cref="HttpMessageHandler"/> rather than a real socket.
/// </summary>
public sealed class CrmHttpGatewayTests
{
    private const string SoapEndpoint = "http://sap.example.com:5040/integration/sap";

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

    private static CrmHttpGateway CreateGateway(RecordingHandler handler, bool configureEndpoint = true, string? messageLogDirectory = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://crm.example.com/") };
        var secretResolver = new FakeSecretResolver().Set("CrmClientSecret", "test-token");
        var options = Options.Create(new CrmOptions
        {
            CredentialSecretName = "CrmClientSecret",
            Soap = new CrmSoapOptions
            {
                Endpoint = configureEndpoint ? new Uri(SoapEndpoint) : null,
                MessageLogDirectory = messageLogDirectory
            }
        });

        return new CrmHttpGateway(client, options, secretResolver, NullLogger<CrmHttpGateway>.Instance);
    }

    private static CreateCrmCustomerCommand CustomerCommand(string publicId = "TRING001") =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", publicId, DateTimeOffset.UtcNow),
            publicId, "Alpha", "L12345678A", "Contact", "contact@example.com", "+355 672017664", "https://maps.example.com/?q=41.3275,19.8187");

    private static CreateActivationTicketCommand TicketCommand() =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "ISP_1", DateTimeOffset.UtcNow),
            "ISP_1", "CUST-1", "BP-1", "REQUEST_FOR_ACTIVATION", "BITSTREAM_STD", 12,
            "41.3275,19.8187", 41.3275m, 19.8187m, "Test koti nga SOAP", "5100020013");

    private static CreateComplaintTicketCommand ComplaintCommand() =>
        new(
            new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "TKT_1", DateTimeOffset.UtcNow),
            "TKT_1", "BP-1", "CONTRACT-1", "SUB-1", "CONNECTIVITY", "NO_SIGNAL", "FIBRE_CUT", "No signal");

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
        Assert.Equal(new Uri(SoapEndpoint), handler.LastRequest.RequestUri);

        var envelope = XDocument.Parse(handler.LastBody!);
        Assert.Equal("CRM_BP_CREATE", envelope.Descendants("code").Single().Value);

        var context = envelope.Descendants("context").Single();
        Assert.Equal("requestContext", context.Attribute("name")!.Value);

        var values = context.Elements("string").ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal("https://maps.example.com/?q=41.3275,19.8187", values["GEOLOCATION"]);
        Assert.Equal("contact@example.com", values["EMAIL"]);
        Assert.Equal("L12345678A", values["IDNUMBER"]);
        // NAME is the request's identifier, ticket code directly followed by the number.
        Assert.Equal("TRING001", values["NAME"]);
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

            var logFile = Assert.Single(Directory.GetFiles(directory, "crm-soap-*.log"));
            var logText = await File.ReadAllTextAsync(logFile);

            // The request exactly as sent is in the file, and the link round-trips through XML.
            Assert.Contains(handler.LastBody!, logText, StringComparison.Ordinal);
            var geolocation = XDocument.Parse(handler.LastBody!).Descendants("string")
                .Single(e => e.Attribute("name")!.Value == "GEOLOCATION").Value;
            Assert.Equal(mapsLink, geolocation);

            Assert.Contains("CRM_BP_CREATE RESPONSE HTTP 200", logText, StringComparison.Ordinal);
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
    public async Task An_unconfigured_SOAP_endpoint_is_a_retryable_technical_failure()
    {
        var handler = new RecordingHandler();
        var gateway = CreateGateway(handler, configureEndpoint: false);

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
    public async Task CreateActivationTicketAsync_posts_the_BITSTREAM_TICKET_CREATE_envelope_and_returns_EV_TICKET_NO()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse(
                "0",
                "<string name=\"EV_MSG\">Service Ticket Created Successfully.</string>" +
                "<string name=\"EV_TICKET_NO\">8009521719</string><string name=\"EV_SUCCESS\">X</string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("8009521719", result.Value!.CrmTicketId);
        Assert.Equal(new Uri(SoapEndpoint), handler.LastRequest!.RequestUri);

        var envelope = XDocument.Parse(handler.LastBody!);
        Assert.Equal("BITSTREAM_TICKET_CREATE", envelope.Descendants("code").Single().Value);

        var values = envelope.Descendants("context").Single().Elements("string")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal("BP-1", values["BP_NO"]);
        Assert.Equal("5100020013", values["CLASS_3"]);
        Assert.Equal("Test koti nga SOAP", values["NOTE"]);
    }

    [Fact]
    public async Task A_ticket_response_without_EV_SUCCESS_X_is_a_business_rejection_carrying_EV_MSG()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse(
                "0",
                "<string name=\"EV_MSG\">Business partner not found.</string><string name=\"EV_SUCCESS\"></string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Contains("Business partner not found.", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ticket_with_EV_SUCCESS_X_but_no_ticket_number_is_not_a_success()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse("0", "<string name=\"EV_SUCCESS\">X</string>")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateActivationTicketAsync(TicketCommand());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task A_ticket_without_a_package_duration_code_is_rejected_without_calling_CRM()
    {
        var handler = new RecordingHandler();
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateActivationTicketAsync(TicketCommand() with { OfferCode = null });

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task CreateComplaintTicketAsync_sends_the_idempotency_key_and_bearer_credential()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonContent.Create(new { crmTicketId = "TKT-1" })
            }
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreateComplaintTicketAsync(ComplaintCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("TKT-1", result.Value!.CrmTicketId);

        Assert.Equal("TKT_1", handler.LastRequest!.Headers.GetValues("Idempotency-Key").Single());
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

        var result = await gateway.CreateComplaintTicketAsync(ComplaintCommand());

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

        var result = await gateway.CreateComplaintTicketAsync(ComplaintCommand());

        Assert.Equal(IntegrationOutcome.TechnicalFailure, result.Outcome);
        Assert.True(result.IsRetryable);
    }

    // --- INT-CRM-10 executeWorkflow BITSTREAM_TO_CRM --------------------------------------

    private static OperatorConfirmationCommand ConfirmationCommand(string confirmed, string? orderTicket = "8009521846") =>
        new(new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "TRINGGPON018:operator-confirmation", DateTimeOffset.UtcNow),
            "TRINGGPON018", "8009521719", confirmed, "Some notes for testing purposes", DateTimeOffset.UtcNow, orderTicket);

    private static HttpResponseMessage WorkflowResponse(string evSuccess, string evMessage) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body>" +
                "<ns2:executeWorkflowResponse xmlns:ns2=\"http://workflow.pi.altima.hr/\"><workflowExecutionResponse>" +
                "<responseCode>0</responseCode><executionStatus>0</executionStatus><runId>40910</runId><context>" +
                "<string name=\"EvAlreadyExists\"/>" +
                $"<string name=\"EvMessage\">{evMessage}</string>" +
                $"<string name=\"EvSuccess\">{evSuccess}</string>" +
                "<string name=\"EvServiceticketid\">8009521846</string>" +
                "</context></workflowExecutionResponse></ns2:executeWorkflowResponse></S:Body></S:Envelope>",
                Encoding.UTF8,
                "text/xml")
        };

    [Theory]
    [InlineData("Y", "Confirmed", "Close")]
    [InlineData("N", "Notconfirmed", "Open")]
    public async Task Operator_confirmation_runs_the_BITSTREAM_TO_CRM_workflow_on_the_order_ticket(string confirmed, string confirmation, string status)
    {
        var handler = new RecordingHandler { Respond = _ => WorkflowResponse("X", "Order successfully saved !") };
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOperatorConfirmationAsync(ConfirmationCommand(confirmed));

        Assert.True(result.IsSuccess);
        Assert.Equal("8009521846", result.Value!.CrmReference);
        Assert.Equal("Order successfully saved !", result.Value.Message);
        Assert.Equal("40910", result.Value.RunId);

        var envelope = XDocument.Parse(handler.LastBody!);
        XNamespace workflow = "http://workflow.pi.altima.hr/";
        Assert.Single(envelope.Descendants(workflow + "executeWorkflow"));
        Assert.Equal("BITSTREAM_TO_CRM", envelope.Descendants("workflowExecutionRequest").Single().Element("code")!.Value);

        var context = envelope.Descendants("context").Single();
        Assert.Equal("?", context.Attribute("name")!.Value);
        var values = context.Elements("string").ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal("8009521846", values["TicketId"]);
        Assert.Equal("TRINGGPON018", values["Username"]);
        Assert.Equal(confirmation, values["BitstreamConfirmation"]);
        Assert.Equal(status, values["Status"]);
        Assert.Equal("Some notes for testing purposes", values["Note"]);
    }

    [Fact]
    public async Task Operator_confirmation_without_EvSuccess_X_is_a_business_rejection_carrying_EvMessage()
    {
        var handler = new RecordingHandler { Respond = _ => WorkflowResponse(string.Empty, "Ticket not found") };
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOperatorConfirmationAsync(ConfirmationCommand("Y"));

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Contains("Ticket not found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operator_confirmation_without_an_order_ticket_is_rejected_without_calling_CRM()
    {
        var handler = new RecordingHandler();
        var gateway = CreateGateway(handler);

        var result = await gateway.SubmitOperatorConfirmationAsync(ConfirmationCommand("Y", orderTicket: null));

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.Equal(CrmHttpGateway.NoOrderTicket, result.ErrorCode);
        Assert.Null(handler.LastRequest);
    }

    // --- INT-CRM-11 executeWorkflow BITSTREAM_TO_CRM (post-activation ticket) ---------------

    private static CreatePhoneTicketCommand PhoneTicketCommand() =>
        new(new IntegrationEnvelope(Guid.NewGuid(), "corr-1", "PAR_1", DateTimeOffset.UtcNow), "PAR_1", "Tring_768", "5100010001", "Test koti nga SOAP");

    private static HttpResponseMessage PhoneTicketResponse(string evSuccess, string evMessage, string evAlreadyExists = "", string evServiceTicketId = "8009521751") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "<S:Envelope xmlns:S=\"http://schemas.xmlsoap.org/soap/envelope/\"><S:Body>" +
                "<ns2:executeWorkflowResponse xmlns:ns2=\"http://workflow.pi.altima.hr/\"><workflowExecutionResponse>" +
                "<responseCode>0</responseCode><executionStatus>0</executionStatus><runId>40911</runId><context>" +
                $"<string name=\"EvAlreadyExists\">{evAlreadyExists}</string>" +
                $"<string name=\"EvMessage\">{evMessage}</string>" +
                $"<string name=\"EvSuccess\">{evSuccess}</string>" +
                $"<string name=\"EvServiceticketid\">{evServiceTicketId}</string>" +
                "</context></workflowExecutionResponse></ns2:executeWorkflowResponse></S:Body></S:Envelope>",
                Encoding.UTF8,
                "text/xml")
        };

    [Fact]
    public async Task CreatePhoneTicketAsync_runs_the_BITSTREAM_TO_CRM_workflow_and_returns_the_EvServiceticketid()
    {
        var handler = new RecordingHandler { Respond = _ => PhoneTicketResponse("X", "Service ticket created") };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreatePhoneTicketAsync(PhoneTicketCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("8009521751", result.Value!.TicketId);
        Assert.Equal("Service ticket created", result.Value.Message);
        Assert.False(result.Value.AlreadyExists);

        var envelope = XDocument.Parse(handler.LastBody!);
        XNamespace workflow = "http://workflow.pi.altima.hr/";
        Assert.Single(envelope.Descendants(workflow + "executeWorkflow"));
        Assert.Equal("BITSTREAM_TO_CRM", envelope.Descendants("workflowExecutionRequest").Single().Element("code")!.Value);

        var context = envelope.Descendants("context").Single();
        Assert.Equal("?", context.Attribute("name")!.Value);
        var values = context.Elements("string").ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal(["Username", "ClassificationId3", "Notes"], values.Keys.ToArray());
        Assert.Equal("Tring_768", values["Username"]);
        Assert.Equal("5100010001", values["ClassificationId3"]);
        Assert.Equal("Test koti nga SOAP", values["Notes"]);
    }

    [Fact]
    public async Task A_phone_ticket_answer_with_EvAlreadyExists_X_is_a_success_flagged_as_already_existing()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => PhoneTicketResponse(string.Empty, "Ticket: 8009521751  already exist with status E0007", evAlreadyExists: "X")
        };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreatePhoneTicketAsync(PhoneTicketCommand());

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.AlreadyExists);
        Assert.Equal("8009521751", result.Value.TicketId);
        Assert.Contains("already exist", result.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_phone_ticket_answer_without_EvSuccess_X_is_a_business_rejection_carrying_EvMessage()
    {
        var handler = new RecordingHandler { Respond = _ => PhoneTicketResponse(string.Empty, "Phone number not found", evServiceTicketId: "") };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreatePhoneTicketAsync(PhoneTicketCommand());

        Assert.Equal(IntegrationOutcome.BusinessRejection, result.Outcome);
        Assert.False(result.IsRetryable);
        Assert.Contains("Phone number not found", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_phone_ticket_answer_with_no_EvServiceticketid_is_not_retried_and_names_what_CRM_returned()
    {
        var handler = new RecordingHandler { Respond = _ => PhoneTicketResponse("X", "Classification not valid", evServiceTicketId: "") };
        var gateway = CreateGateway(handler);

        var result = await gateway.CreatePhoneTicketAsync(PhoneTicketCommand());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRetryable);
        Assert.Contains("EvMessage=Classification not valid", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_identifier_issued_before_the_change_is_sent_as_NAME_without_its_underscore()
    {
        var handler = new RecordingHandler
        {
            Respond = _ => SoapResponse("0", "<string name=\"BP_NO\">1102017112</string>")
        };
        var gateway = CreateGateway(handler);

        await gateway.CreateCustomerAsync(CustomerCommand("TRING_003"));

        var values = XDocument.Parse(handler.LastBody!).Descendants("context").Single().Elements("string")
            .ToDictionary(e => e.Attribute("name")!.Value, e => e.Value);
        Assert.Equal("TRING003", values["NAME"]);
    }
}
