using System.Xml;
using System.Xml.Linq;
using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Crm;

/// <summary>
/// Request/response mapping for CRM's SOAP workflow operations (<c>executeOperation</c>):
/// <c>CRM_BP_CREATE</c> (INT-CRM-01) and <c>BITSTREAM_TICKET_CREATE</c> (INT-CRM-02).
/// Built with LINQ to XML so every value is XML-escaped.
/// </summary>
internal static class CrmSoap
{
    private static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Workflow = "http://workflow.pi.altima.hr/";

    // --- INT-CRM-01 CRM_BP_CREATE ---------------------------------------------------------

    public static string BuildCreateBusinessPartnerRequest(CrmBusinessPartnerOptions options, CreateCrmCustomerCommand command) =>
        BuildRequest(
            options.OperationCode,
            ("GEOLOCATION", command.Geolocation),
            ("EMAIL", command.ContactEmail),
            ("IDNUMBER", command.IspNipt),
            // NAME is the request's identifier with a space for the underscore: TRING_001 -> "TRING 001".
            ("NAME", command.RequestPublicId.Replace('_', ' ')),
            ("SHORT_ADDR", string.Empty),
            ("CUSTOMERTYPE", options.CustomerType),
            ("BP_CAT", options.BpCategory),
            ("PARTNERTYPE", options.PartnerType),
            ("MOBILE", NormaliseMobile(command.ContactMobile)));

    /// <summary>
    /// Success only when <c>responseCode</c> is 0 and a <c>BP_NO</c> came back; any other
    /// <c>responseCode</c> is CRM refusing the request, so it is a business rejection.
    /// </summary>
    public static IntegrationResult<CreateCrmCustomerResult> ParseCreateBusinessPartnerResponse(string body)
    {
        var response = ParseResponse(body, "CRM_BP_CREATE");

        if (response.Failure is { } failure)
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(failure);
        }

        if (response.ResponseCode != "0")
        {
            return IntegrationResult<CreateCrmCustomerResult>.BusinessRejection(
                response.ResponseCode!,
                $"CRM rejected Business Partner creation with responseCode {response.ResponseCode}{Describe(response.Context)}.");
        }

        if (!response.Context.TryGetValue("BP_NO", out var bpNumber) || string.IsNullOrEmpty(bpNumber))
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(
                "CRM Business Partner creation returned responseCode 0 but no BP_NO.");
        }

        return IntegrationResult<CreateCrmCustomerResult>.Success(new CreateCrmCustomerResult(bpNumber, bpNumber));
    }

    // --- INT-CRM-02 BITSTREAM_TICKET_CREATE -----------------------------------------------

    public static string BuildCreateTicketRequest(CrmTicketOptions options, CreateActivationTicketCommand command) =>
        BuildRequest(
            options.OperationCode,
            ("BP_NO", command.BusinessPartner),
            ("CLASS_3", command.OfferCode),
            ("NOTE", command.Comments));

    /// <summary>
    /// Success only when <c>responseCode</c> is 0 and <c>EV_SUCCESS</c> is X; the ticket number is
    /// <c>EV_TICKET_NO</c>. Anything else CRM answered is a business rejection carrying
    /// <c>EV_MSG</c>.
    /// </summary>
    public static IntegrationResult<CreateCrmTicketResult> ParseCreateTicketResponse(string body)
    {
        var response = ParseResponse(body, "BITSTREAM_TICKET_CREATE");

        if (response.Failure is { } failure)
        {
            return IntegrationResult<CreateCrmTicketResult>.TechnicalFailure(failure);
        }

        response.Context.TryGetValue("EV_SUCCESS", out var success);
        response.Context.TryGetValue("EV_MSG", out var message);

        if (response.ResponseCode != "0" || !string.Equals(success, "X", StringComparison.Ordinal))
        {
            var reason = string.IsNullOrEmpty(message) ? "no EV_MSG" : message;

            return IntegrationResult<CreateCrmTicketResult>.BusinessRejection(
                response.ResponseCode!,
                $"CRM did not create the activation ticket (responseCode {response.ResponseCode}, EV_SUCCESS '{success}'): {reason}");
        }

        if (!response.Context.TryGetValue("EV_TICKET_NO", out var ticketNumber) || string.IsNullOrEmpty(ticketNumber))
        {
            return IntegrationResult<CreateCrmTicketResult>.TechnicalFailure(
                "CRM reported the activation ticket as created but returned no EV_TICKET_NO.");
        }

        return IntegrationResult<CreateCrmTicketResult>.Success(new CreateCrmTicketResult(ticketNumber));
    }

    // --- Shared -----------------------------------------------------------------------------

    /// <summary>The SOAP Fault's faultstring, when <paramref name="body"/> is a readable fault.</summary>
    public static string? TryReadFault(string body)
    {
        try
        {
            return FindFault(XDocument.Parse(body));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>MOBILE is sent without the Albanian country code.</summary>
    internal static string NormaliseMobile(string mobile)
    {
        var compact = string.Concat(mobile.Where(c => !char.IsWhiteSpace(c)));

        if (compact.StartsWith("+355", StringComparison.Ordinal))
        {
            return compact[4..];
        }

        return compact.StartsWith("00355", StringComparison.Ordinal) ? compact[5..] : compact;
    }

    private static string BuildRequest(string operationCode, params (string Name, string? Value)[] context)
    {
        var envelope = new XElement(
            SoapEnv + "Envelope",
            new XAttribute(XNamespace.Xmlns + "soapenv", SoapEnv),
            new XAttribute(XNamespace.Xmlns + "wor", Workflow),
            new XElement(SoapEnv + "Header"),
            new XElement(
                SoapEnv + "Body",
                new XElement(
                    Workflow + "executeOperation",
                    new XElement(
                        "operationExecutionRequest",
                        new XElement("code", operationCode),
                        new XElement(
                            "context",
                            new XAttribute("name", "requestContext"),
                            context.Select(item => new XElement("string", new XAttribute("name", item.Name), item.Value ?? string.Empty)))))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// The <c>responseCode</c> and the <c>context</c> name/value strings of an
    /// <c>executeOperationResponse</c>, or <see cref="SoapResponse.Failure"/> when the body is
    /// unreadable, a SOAP fault, or carries no responseCode.
    /// </summary>
    private static SoapResponse ParseResponse(string body, string operationCode)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(body);
        }
        catch (XmlException exception)
        {
            return SoapResponse.Failed($"CRM {operationCode} returned an unreadable response: {exception.Message}");
        }

        if (FindFault(document) is { } fault)
        {
            return SoapResponse.Failed(fault);
        }

        var response = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "operationExecutionResponse");
        var responseCode = response?.Elements().FirstOrDefault(e => e.Name.LocalName == "responseCode")?.Value.Trim();

        if (response is null || responseCode is null)
        {
            return SoapResponse.Failed($"CRM {operationCode} response has no responseCode.");
        }

        var context = response.Elements().FirstOrDefault(e => e.Name.LocalName == "context")?
            .Elements()
            .Where(e => e.Name.LocalName == "string" && e.Attribute("name") is not null)
            .GroupBy(e => e.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        return new SoapResponse(responseCode, context, null);
    }

    private static string Describe(IReadOnlyDictionary<string, string> context) =>
        context.Count == 0 ? string.Empty : " (" + string.Join(", ", context.Select(pair => $"{pair.Key}={pair.Value}")) + ")";

    private static string? FindFault(XDocument document)
    {
        var fault = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");

        if (fault is null)
        {
            return null;
        }

        var faultString = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim();
        return $"CRM returned a SOAP fault: {(string.IsNullOrEmpty(faultString) ? "(no faultstring)" : faultString)}";
    }

    private sealed record SoapResponse(string? ResponseCode, IReadOnlyDictionary<string, string> Context, string? Failure)
    {
        public static SoapResponse Failed(string failure) =>
            new(null, new Dictionary<string, string>(StringComparer.Ordinal), failure);
    }
}
