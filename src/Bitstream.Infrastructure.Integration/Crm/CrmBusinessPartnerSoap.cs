using System.Xml;
using System.Xml.Linq;
using Bitstream.Application.Abstractions.Integration;

namespace Bitstream.Infrastructure.Integration.Crm;

/// <summary>
/// Request/response mapping for CRM's <c>CRM_BP_CREATE</c> SOAP operation (INT-CRM-01).
/// Built with LINQ to XML so every value is XML-escaped.
/// </summary>
internal static class CrmBusinessPartnerSoap
{
    private static readonly XNamespace SoapEnv = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Workflow = "http://workflow.pi.altima.hr/";

    public static string BuildCreateRequest(CrmBusinessPartnerOptions options, CreateCrmCustomerCommand command)
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
                        new XElement("code", options.OperationCode),
                        new XElement(
                            "context",
                            new XAttribute("name", "requestContext"),
                            ContextValue("GEOLOCATION", command.Geolocation),
                            ContextValue("EMAIL", command.ContactEmail),
                            ContextValue("IDNUMBER", command.IspNipt),
                            ContextValue("NAME", command.RequestPublicId.Replace('_', ' ')),
                            ContextValue("SHORT_ADDR", string.Empty),
                            ContextValue("CUSTOMERTYPE", options.CustomerType),
                            ContextValue("BP_CAT", options.BpCategory),
                            ContextValue("PARTNERTYPE", options.PartnerType),
                            ContextValue("MOBILE", NormaliseMobile(command.ContactMobile)))))));

        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Success only when <c>responseCode</c> is 0 and a <c>BP_NO</c> came back; any other
    /// <c>responseCode</c> is CRM refusing the request, so it is a business rejection.
    /// </summary>
    public static IntegrationResult<CreateCrmCustomerResult> ParseCreateResponse(string body)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(body);
        }
        catch (XmlException exception)
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(
                $"CRM Business Partner creation returned an unreadable response: {exception.Message}");
        }

        if (FindFault(document) is { } fault)
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(fault);
        }

        var response = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "operationExecutionResponse");
        var responseCode = response?.Elements().FirstOrDefault(e => e.Name.LocalName == "responseCode")?.Value.Trim();

        if (responseCode is null)
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(
                "CRM Business Partner creation response has no responseCode.");
        }

        var context = response!.Elements().FirstOrDefault(e => e.Name.LocalName == "context")?
            .Elements()
            .Where(e => e.Name.LocalName == "string" && e.Attribute("name") is not null)
            .GroupBy(e => e.Attribute("name")!.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        if (responseCode != "0")
        {
            var details = context.Count == 0
                ? string.Empty
                : " (" + string.Join(", ", context.Select(pair => $"{pair.Key}={pair.Value}")) + ")";

            return IntegrationResult<CreateCrmCustomerResult>.BusinessRejection(
                responseCode, $"CRM rejected Business Partner creation with responseCode {responseCode}{details}.");
        }

        if (!context.TryGetValue("BP_NO", out var bpNumber) || string.IsNullOrEmpty(bpNumber))
        {
            return IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure(
                "CRM Business Partner creation returned responseCode 0 but no BP_NO.");
        }

        return IntegrationResult<CreateCrmCustomerResult>.Success(new CreateCrmCustomerResult(bpNumber, bpNumber));
    }

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

    private static XElement ContextValue(string name, string? value) =>
        new("string", new XAttribute("name", name), value ?? string.Empty);

    private static string? FindFault(XDocument document)
    {
        var fault = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");

        if (fault is null)
        {
            return null;
        }

        var faultString = fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "faultstring")?.Value.Trim();
        return $"CRM Business Partner creation returned a SOAP fault: {(string.IsNullOrEmpty(faultString) ? "(no faultstring)" : faultString)}";
    }
}
