using System.Collections.Concurrent;
using System.Text.Json.Serialization;

// A stand-in for the real CRM system (TRD 11.4 open item 1), implementing the provisional
// TRD §7.4 payload shape that CrmHttpGateway (Bitstream.Infrastructure.Integration) calls
// against. Point Integration:Crm:BaseAddress at this host's URL for local development —
// there is no real CRM endpoint to point at yet.
//
// Run: dotnet run --project tools/CrmSimulator
// Then set, e.g.: BITSTREAM_Integration__Crm__BaseAddress=https://localhost:5199
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Idempotency (TR-INT-03, TR-INT-17): the same Idempotency-Key on the same path always gets
// back the response it got the first time, exactly what a real idempotent CRM would do — so
// a retried outbox message never creates a second customer or ticket.
var customerResponses = new ConcurrentDictionary<string, CustomerResponse>();
var ticketResponses = new ConcurrentDictionary<string, TicketResponse>();
var customerSequence = 0;
var ticketSequence = 0;

app.MapMethods("/", ["GET", "HEAD"], () => Results.Ok(new { service = "CrmSimulator", status = "up" }));

app.MapPost("/customers", (CustomerRequest request, HttpRequest httpRequest) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault() ?? request.RequestPublicId;

    var response = customerResponses.GetOrAdd(idempotencyKey, _ =>
    {
        var sequence = Interlocked.Increment(ref customerSequence);
        return new CustomerResponse($"CRMCUST-{sequence:D6}", $"BP-{sequence:D6}");
    });

    return Results.Created($"/customers/{response.CrmCustomerId}", response);
});

app.MapPost("/tickets", (ActivationTicketRequest request, HttpRequest httpRequest) =>
{
    var idempotencyKey = httpRequest.Headers["Idempotency-Key"].FirstOrDefault() ?? request.RequestPublicId;

    var response = ticketResponses.GetOrAdd(idempotencyKey, _ =>
    {
        var sequence = Interlocked.Increment(ref ticketSequence);
        return new TicketResponse($"CRMTKT-{sequence:D6}");
    });

    return Results.Created($"/tickets/{response.CrmTicketId}", response);
});

app.Run();

internal sealed record CustomerRequest(
    [property: JsonPropertyName("requestPublicId")] string RequestPublicId,
    [property: JsonPropertyName("ispName")] string IspName,
    [property: JsonPropertyName("ispNipt")] string IspNipt,
    [property: JsonPropertyName("contactPerson")] string ContactPerson,
    [property: JsonPropertyName("contactEmail")] string ContactEmail,
    [property: JsonPropertyName("contactMobile")] string ContactMobile);

internal sealed record CustomerResponse(
    [property: JsonPropertyName("crmCustomerId")] string CrmCustomerId,
    [property: JsonPropertyName("businessPartner")] string BusinessPartner);

internal sealed record ActivationTicketRequest(
    [property: JsonPropertyName("requestPublicId")] string RequestPublicId,
    [property: JsonPropertyName("crmCustomerId")] string CrmCustomerId,
    [property: JsonPropertyName("businessPartner")] string BusinessPartner,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("packageCode")] string PackageCode,
    [property: JsonPropertyName("contractDurationMonths")] int ContractDurationMonths,
    [property: JsonPropertyName("locationRaw")] string LocationRaw,
    [property: JsonPropertyName("locationLat")] decimal LocationLat,
    [property: JsonPropertyName("locationLng")] decimal LocationLng,
    [property: JsonPropertyName("comments")] string? Comments);

internal sealed record TicketResponse([property: JsonPropertyName("crmTicketId")] string CrmTicketId);
