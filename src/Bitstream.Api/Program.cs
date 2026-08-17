using Bitstream.Api.Configuration;
using Bitstream.Api.Endpoints;
using Bitstream.Api.Middleware;
using Bitstream.Application;
using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Infrastructure.Integration;
using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------------------
// TR-ARC-06: everything environment-specific is configuration. Environment variables are
// added last so that IIS application-pool settings override the shipped defaults without any
// file on the server being edited by hand (TR-ARC-08).
builder.Configuration.AddEnvironmentVariables(prefix: "BITSTREAM_");

// --- Composition root ------------------------------------------------------------------
// The only place in the solution that knows all four layers exist (TRD 2.1).
builder.Services.AddBitstreamApplication(builder.Configuration);
builder.Services.AddBitstreamPersistence(builder.Configuration);
builder.Services.AddBitstreamIntegration(builder.Configuration);

// TR-SEC-28: adapters ask for a secret by name; this decides where names resolve on this host.
builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();

builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

// Every options set is resolved once before the first request, so a configuration mistake
// fails the deployment rather than the first user who touches the affected feature.
builder.Services.AddSingleton<IHostedService>(provider => new OptionsStartupValidator(
    provider,
    [
        .. DependencyInjection.ValidatedOptionTypes,
        .. Bitstream.Infrastructure.Persistence.DependencyInjection.ValidatedOptionTypes,
        .. Bitstream.Infrastructure.Integration.DependencyInjection.ValidatedOptionTypes,
        typeof(RateLimitOptions)
    ],
    provider.GetRequiredService<ILogger<OptionsStartupValidator>>()));

// --- Presentation ----------------------------------------------------------------------
builder.Services.AddProblemDetails();

// TR-INT-01: the interface contract is generated from the endpoint definitions, so the
// published document cannot drift from the implementation.
builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new()
        {
            Title = "Bitstream Portal API",
            Version = "v1",
            Description =
                "Portal-exposed interfaces of the ISP Platform (Bitstream Portal), TRD v1.0 section 7.1. " +
                "All endpoints are stubs at scaffold stage and return 501 Not Implemented. " +
                "Authentication of the inbound CRM interface — mutual TLS or a signed bearer token — " +
                "is TRD 11.4 open item 3 and is not yet configured."
        };
        return Task.CompletedTask;
    });
});

// TR-SEC-29 and TR-INT-30: rate limiting on authentication, on creation endpoints and on
// the CRM inbound interface. Limits are configuration, not code (TR-ARC-06).
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

    options.AddFixedWindowLimiter(RateLimitPolicies.Administration, limiter =>
    {
        limiter.PermitLimit = rateLimits.Administration.PermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimits.Administration.WindowSeconds);
        limiter.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// TR-ARC-05: health endpoints report the reachability of the dependencies. Individual
// checks for database, CRM, BI and SMTP are registered by each adapter as it is built.
builder.Services.AddHealthChecks();

var app = builder.Build();

// --- Pipeline --------------------------------------------------------------------------
// TR-ARC-04: every request is assigned a correlation ID, propagated downstream and written
// to every log entry. This runs first so that nothing is logged without one.
app.UseMiddleware<CorrelationIdMiddleware>();

// One structured entry per request with status and duration. Sits inside the correlation
// middleware so its entries carry the ID, and outside the exception handler so a failed
// request is still logged with its duration.
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

// The vanilla-JS frontend is served by this host so that the portal is a single IIS site and
// the session cookie is same-origin. On publish, src/Bitstream.Web/wwwroot is copied into
// wwwroot (see the AddFrontendToPublish target); in Development it is served from source so
// that `npm run watch:css` shows up on refresh.
if (app.Environment.IsDevelopment())
{
    var frontendRoot = Path.GetFullPath(
        Path.Combine(app.Environment.ContentRootPath, "..", "Bitstream.Web", "wwwroot"));

    if (Directory.Exists(frontendRoot))
    {
        var frontendFiles = new PhysicalFileProvider(frontendRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });
    }
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

// Generated contract at /openapi/v1.json. Exposed in all environments because CRM and the
// integration team consume it; it describes stubs and carries no data (TR-INT-01).
app.MapOpenApi();

app.MapCrmInboundEndpoints();
app.MapOperationsEndpoints();
app.MapHealthEndpoints();

app.Run();

/// <summary>Named rate-limiting policies, referenced by the endpoint groups.</summary>
internal static class RateLimitPolicies
{
    public const string CrmInbound = "crm-inbound";

    public const string Administration = "administration";
}
