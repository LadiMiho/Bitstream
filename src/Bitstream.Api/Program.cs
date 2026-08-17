using Bitstream.Api.Endpoints;
using Bitstream.Api.Middleware;
using Bitstream.Application;
using Bitstream.Infrastructure.Integration;
using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// --- Composition root ------------------------------------------------------------------
// The only place in the solution that knows all four layers exist (TRD 2.1).
builder.Services.AddBitstreamApplication();
builder.Services.AddBitstreamPersistence(builder.Configuration);
builder.Services.AddBitstreamIntegration(builder.Configuration);

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
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(RateLimitPolicies.CrmInbound, limiter =>
    {
        limiter.PermitLimit = builder.Configuration.GetValue("RateLimits:CrmInbound:PermitLimit", 200);
        limiter.Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimits:CrmInbound:WindowSeconds", 1));
        limiter.QueueLimit = 0;
    });

    options.AddFixedWindowLimiter(RateLimitPolicies.Administration, limiter =>
    {
        limiter.PermitLimit = builder.Configuration.GetValue("RateLimits:Administration:PermitLimit", 60);
        limiter.Window = TimeSpan.FromSeconds(builder.Configuration.GetValue("RateLimits:Administration:WindowSeconds", 60));
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
