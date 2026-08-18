using Bitstream.Api.Endpoints;
using Bitstream.Application;
using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Endpoints;
using Bitstream.Hosting.Middleware;
using Bitstream.Hosting.Security;
using Bitstream.Infrastructure.Integration;
using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

// The integration host: CRM communication and nothing else.
//
// Inbound  — the single versioned event API CRM posts to (TR-INT-22), TRD 7.1 rows INT-CRM-03,
//            -05, -06 (inbound half) and -07.
// Outbound — the outbox dispatcher, which is what actually calls CRM (INT-CRM-01, -02, -04,
//            -06, -08, -09), plus the BI active-lines sync and the auto-confirmation sweep.
//
// No Razor Pages, no session cookie, no portal endpoints: people use Bitstream.Web, machines
// use this. The two hosts share a database, not a process.
var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------------------
// TR-ARC-06 / TR-ARC-08, as in the portal host.
builder.Configuration.AddEnvironmentVariables(prefix: "BITSTREAM_");

// --- Composition root ------------------------------------------------------------------
builder.Services.AddBitstreamApplication(builder.Configuration);
builder.Services.AddBitstreamPersistence(builder.Configuration);
builder.Services.AddBitstreamIntegration(builder.Configuration);

// This host runs the recurring work. Exactly one host may — see the remarks on the method.
builder.Services.AddBitstreamBackgroundJobs();

builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();

// Nothing here acts for a portal user: CRM posts events, and the background jobs run on a
// timer. The audit log records that honestly rather than attributing the change to whichever
// user happened to be nearby (TR-SEC-22).
builder.Services.AddSingleton<ICurrentUserContext, SystemCurrentUserContext>();

builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

builder.Services.AddSingleton<IHostedService>(provider => new OptionsStartupValidator(
    provider,
    [
        .. Bitstream.Application.DependencyInjection.ValidatedOptionTypes,
        .. Bitstream.Infrastructure.Persistence.DependencyInjection.ValidatedOptionTypes,
        .. Bitstream.Infrastructure.Integration.DependencyInjection.ValidatedOptionTypes,
        typeof(RateLimitOptions)
    ],
    provider.GetRequiredService<ILogger<OptionsStartupValidator>>()));

// --- Presentation ----------------------------------------------------------------------
builder.Services.AddProblemDetails();

// TR-INT-01: the interface contract is generated from the endpoint definitions, so the
// published document cannot drift from the implementation. It lives on this host because the
// CRM-facing interface is the only contract another system consumes.
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new()
        {
            Title = "Bitstream Portal — CRM integration API",
            Version = "v1",
            Description =
                "The CRM-facing interface of the ISP Platform (Bitstream Portal), TRD v1.0 " +
                "section 7.3.2. This host serves the inbound ticket event API only; the portal's " +
                "own screens and the endpoints they call are served by Bitstream.Web and are not " +
                "part of this contract. Authentication of the inbound interface — mutual TLS or a " +
                "signed bearer token — is TRD 11.4 open item 3 and is not yet configured."
        };
        return Task.CompletedTask;
    });
});

var rateLimits = builder.Configuration
    .GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(RateLimitPolicies.CrmInbound, limiter =>
    {
        limiter.PermitLimit = rateLimits.CrmInbound.PermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimits.CrmInbound.WindowSeconds);
        limiter.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// TR-ARC-05: each layer contributes the checks for the dependencies it owns.
builder.Services.AddHealthChecks()
    .AddBitstreamPersistenceHealthChecks()
    .AddBitstreamIntegrationHealthChecks();

var app = builder.Build();

// --- Pipeline --------------------------------------------------------------------------
// TR-ARC-04: correlation first, so nothing is logged without an ID.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (!app.Environment.IsDevelopment())
{
    // TR-SEC-26: TLS 1.2 or higher; plain HTTP is refused for API endpoints.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

// Generated contract at /openapi/v1.json. Exposed in all environments because CRM and the
// integration team consume it (TR-INT-01).
app.MapOpenApi();

app.MapCrmInboundEndpoints();
app.MapHealthEndpoints();

await app.RunAsync().ConfigureAwait(false);
