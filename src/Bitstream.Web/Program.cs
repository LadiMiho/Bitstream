using Bitstream.Application;
using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Endpoints;
using Bitstream.Hosting.Middleware;
using Bitstream.Infrastructure.Integration;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web;
using Bitstream.Web.Endpoints;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

// The portal site: everything a person interacts with. Razor Pages for the screens, plus the
// JSON endpoints those screens' own scripts call — both served from this host, same origin, so
// the session cookie works with no CORS configuration.
//
// It deliberately does NOT expose the CRM-facing interface, and does NOT run the background
// jobs; both belong to Bitstream.Api. See AddBitstreamBackgroundJobs for why exactly one host
// may run them.
var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---------------------------------------------------------------------
// TR-ARC-06: everything environment-specific is configuration. Environment variables are
// added last so that IIS application-pool settings override the shipped defaults without any
// file on the server being edited by hand (TR-ARC-08).
builder.Configuration.AddEnvironmentVariables(prefix: "BITSTREAM_");

// --- Composition root ------------------------------------------------------------------
builder.Services.AddBitstreamApplication(builder.Configuration);
builder.Services.AddBitstreamPersistence(builder.Configuration);
builder.Services.AddBitstreamIntegration(builder.Configuration);

// TR-SEC-28: adapters ask for a secret by name; this decides where names resolve on this host.
builder.Services.AddSingleton<ISecretResolver, ConfigurationSecretResolver>();

// --- Identity and access (TRD 4) --------------------------------------------------------
// TR-SEC-07: authentication is ASP.NET Core Identity's own cookie authentication
// (AddIdentity<User, Role>() in Bitstream.Infrastructure.Persistence.DependencyInjection wires
// SignInManager and the cookie scheme; this section only configures it). This is also what turns
// HttpCurrentUserContext's claims into the ambient identity every application service
// authorises against (TR-SEC-17 to TR-SEC-19) — BitstreamClaimsPrincipalFactory is what adds
// IspId/permission claims to the identity Identity itself builds.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, HttpCurrentUserContext>();
builder.Services.AddScoped<IUserClaimsPrincipalFactory<User>, BitstreamClaimsPrincipalFactory>();

// SessionOptions itself is bound/validated by AddBitstreamApplication above; read directly here
// too, since cookie configuration below runs at startup composition time, before DI resolves.
var sessionOptions = builder.Configuration.GetSection(Bitstream.Application.Configuration.SessionOptions.SectionName)
    .Get<Bitstream.Application.Configuration.SessionOptions>() ?? new Bitstream.Application.Configuration.SessionOptions();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = sessionOptions.CookieName;
    options.Cookie.HttpOnly = true;
    // TR-SEC-26: the portal is TLS-only, so the session cookie never travels in the clear.
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    // Same-origin, single-page frontend served by this same host — Strict is safe and is the
    // tightest setting available.
    options.Cookie.SameSite = SameSiteMode.Strict;

    // TR-SEC-07: idle timeout — the cookie slides forward on each request, exactly like the
    // custom UserSession.LastActivityAt design it replaces.
    options.ExpireTimeSpan = sessionOptions.IdleTimeout;
    options.SlidingExpiration = true;

    // JSON API, not a browser-redirect login flow (AuthEndpoints.cs handles 401/403 itself) —
    // the default RedirectToLogin/AccessDenied behaviour would otherwise turn an API 401 into a
    // 302 to a page.
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };

    // Compose with Identity's own security-stamp check (registered by AddIdentity) rather than
    // replacing it: run it first, then enforce TR-SEC-07's absolute cap — "whichever of the two
    // is reached first" — which cookie auth has no native concept of on its own.
    var identityValidator = options.Events.OnValidatePrincipal;

    options.Events.OnValidatePrincipal = async context =>
    {
        await identityValidator(context).ConfigureAwait(false);

        if (context.Principal is null)
        {
            return;
        }

        var issuedUtc = context.Properties.IssuedUtc;
        var clock = context.HttpContext.RequestServices.GetRequiredService<IClock>();

        if (issuedUtc is not null && clock.UtcNow - issuedUtc.Value > sessionOptions.AbsoluteTimeout)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).ConfigureAwait(false);
        }
    };
});

// TR-SEC-17: "fresh every request" — re-fetches the user and rebuilds claims
// (BitstreamClaimsPrincipalFactory) on every single request rather than the framework default
// (every 30 minutes), so a permission or lockout change takes effect on the very next request.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

// TR-SEC-17: every permission requirement is evaluated by this one handler, reading the claims
// BitstreamClaimsPrincipalFactory set. No screen may substitute a client-side check for it (TR-SEC-20).
builder.Services.AddAuthorization();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddOptions<RateLimitOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();

// Every options set is resolved once before the first request, so a configuration mistake
// fails the deployment rather than the first user who touches the affected feature.
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

// Folder-based Razor Pages under /Pages, one module per folder, styled with Tailwind
// (ClientAssets/app.css, compiled to wwwroot/css/app.css). The auth-guard is SecurePageModel,
// a page filter every protected page derives from; it is not a client-side redirect
// (TR-SEC-20). Vanilla JavaScript is for client-side behaviour only and never owns navigation.
builder.Services.AddRazorPages();

// MVC controllers + views under /Controllers and /Views, alongside Razor Pages above — the two
// coexist in the same host with no routing conflict, since nothing maps the same path twice.
// User Administration (Controllers/UsersController.cs) is the first screen built this way: a
// grid plus drawer forms (add/edit/view/change password) rendered as server-side partial views,
// exactly the same auth-guard discipline as SecurePageModel (RequireSessionAttribute /
// RequirePermissionAttribute, Security/MvcAuthorization.cs) and the same rule as every other
// screen that a write only ever happens through the JSON API, never through model binding here.
builder.Services.AddControllersWithViews();

var rateLimits = builder.Configuration
    .GetSection(RateLimitOptions.SectionName)
    .Get<RateLimitOptions>() ?? new RateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(RateLimitPolicies.Administration, limiter =>
    {
        limiter.PermitLimit = rateLimits.Administration.PermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimits.Administration.WindowSeconds);
        limiter.QueueLimit = 0;
    });

    // TR-SEC-29: tighter than Administration on purpose — this is exactly where a
    // credential-stuffing or lockout-triggering attempt would land.
    options.AddFixedWindowLimiter(RateLimitPolicies.Authentication, limiter =>
    {
        limiter.PermitLimit = rateLimits.Authentication.PermitLimit;
        limiter.Window = TimeSpan.FromSeconds(rateLimits.Authentication.WindowSeconds);
        limiter.QueueLimit = 0;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// TR-ARC-05: each layer contributes the checks for the dependencies it owns.
builder.Services.AddHealthChecks()
    .AddBitstreamPersistenceHealthChecks()
    .AddBitstreamIntegrationHealthChecks();

var app = builder.Build();

// --- Development conveniences ------------------------------------------------------------
// Applies db/mssql and seeds the local administrator. Strictly Development-only and opt-in;
// see DevelopmentBootstrapper for why it can never run anywhere else.
await app.RunDevelopmentBootstrapAsync().ConfigureAwait(false);

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
    // TR-SEC-26: TLS 1.2 or higher; plain HTTP is refused.
    app.UseHsts();
}

app.UseHttpsRedirection();

// Ahead of authentication/authorization: a CSS file needs neither, and short-circuiting here
// means the request never reaches that pipeline.
app.UseStaticFiles();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapAdministrationEndpoints();
app.MapActivationEndpoints();
app.MapPostActivationEndpoints();
app.MapOperationsEndpoints();

app.MapRazorPages();
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);
