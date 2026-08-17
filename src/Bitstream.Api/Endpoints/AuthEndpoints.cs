using System.Security.Claims;
using Bitstream.Api.Contracts;
using Bitstream.Api.Security;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// TRD 4.1: authentication and 2FA. Two calls per login (see <see cref="IIdentityService"/>),
/// plus logout and a "who am I" endpoint the frontend uses to decide what to render — a
/// decision TR-SEC-20 requires is never trusted on its own, since every subsequent call is
/// authorised again server-side regardless of what the interface shows.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // TR-SEC-29: rate limiting on authentication endpoints specifically, tighter than the
        // general administration limit — this is exactly where a credential-stuffing attempt
        // would land.
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("First factor: email and password")
            .WithDescription(
                "TR-SEC-01, TR-SEC-02, TR-SEC-06, TR-SEC-12. A locked account is rejected " +
                "without checking the password. On success, a second-factor challenge is issued " +
                "and must be completed at POST /login/verify within its validity window.")
            .Accepts<LoginRequest>("application/json")
            .Produces<LoginChallengeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked)
            .AllowAnonymous();

        group.MapPost("/login/verify", VerifyTwoFactorAsync)
            .WithName("VerifyTwoFactor")
            .WithSummary("Second factor: TOTP or one-time code")
            .WithDescription(
                "TR-SEC-04: the code is single-use and valid for at most 5 minutes. On success, " +
                "sets the session as an HttpOnly cookie (TR-SEC-07) and returns the caller's profile.")
            .Accepts<TwoFactorVerifyRequest>("application/json")
            .Produces<SessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        group.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Invalidate the current session")
            .WithDescription("TR-SEC-07: the session token is revoked immediately, not merely forgotten by the client. Idempotent.")
            .Produces(StatusCodes.Status204NoContent)
            .AllowAnonymous();

        group.MapGet("/me", GetCurrentUser)
            .WithName("GetCurrentUser")
            .WithSummary("The authenticated caller's profile and permissions")
            .Produces<CurrentUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest request,
        HttpContext httpContext,
        IIdentityService identityService,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.Problem(
                title: "Invalid request",
                detail: "Email and password are both required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actorIp = httpContext.Connection.RemoteIpAddress?.ToString();
        LoginResult result;

        try
        {
            result = await identityService.AuthenticateAsync(request.Email, request.Password, actorIp, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TwoFactorDeliveryException exception)
        {
            // The detail (which can name the failing relay and its raw error) is logged, never
            // returned: this endpoint is anonymous, and a login failure response is not the
            // place to disclose infrastructure detail (TR-SEC-27).
            logger.LogError(exception, "Second-factor code could not be dispatched for {Email}.", request.Email);

            // Distinguished from an unrelated server fault: the password was correct, but the
            // second factor could not be dispatched right now — a 503 tells the caller to retry
            // rather than that something in the portal is broken.
            return Results.Problem(
                title: "Could not send verification code",
                detail: "The password was correct, but the verification code could not be sent. Please try again shortly.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return result.Outcome switch
        {
            LoginOutcome.ChallengeIssued => Results.Ok(new LoginChallengeResponse(
                result.ChallengeToken!, result.Channel!.Value.ToString(), result.ExpiresAt!.Value)),

            LoginOutcome.AccountLocked => Results.Problem(
                title: "Account locked",
                // TR-NFR-12: specific and actionable. Disclosed deliberately — see LoginOutcome.AccountLocked.
                detail: "This account is locked after too many failed sign-in attempts. Contact your administrator.",
                statusCode: StatusCodes.Status423Locked),

            _ => Results.Problem(
                title: "Invalid credentials",
                detail: "The email or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    private static async Task<IResult> VerifyTwoFactorAsync(
        [FromBody] TwoFactorVerifyRequest request,
        HttpContext httpContext,
        IIdentityService identityService,
        IOptions<SessionOptions> sessionOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeToken) || string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.Problem(
                title: "Invalid request",
                detail: "challengeToken and code are both required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actorIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var result = await identityService.CompleteSecondFactorAsync(request.ChallengeToken, request.Code, actorIp, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome != TwoFactorOutcome.Succeeded)
        {
            var detail = result.Outcome switch
            {
                TwoFactorOutcome.ChallengeExpired => "This code has expired. Start a new sign-in.",
                TwoFactorOutcome.TooManyAttempts => "Too many incorrect attempts. Start a new sign-in.",
                _ => "The code is incorrect."
            };

            return Results.Problem(title: "Verification failed", detail: detail, statusCode: StatusCodes.Status401Unauthorized);
        }

        var options = sessionOptions.Value;

        httpContext.Response.Cookies.Append(options.CookieName, result.SessionToken!, new CookieOptions
        {
            HttpOnly = true,
            // TR-SEC-26: the portal is TLS-only, so the session cookie never travels in the clear.
            Secure = true,
            // Same-origin, single-page frontend served by this same host (see Program.cs) —
            // Strict is safe and is the tightest setting available.
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = result.SessionExpiresAt
        });

        var user = result.User!;

        return Results.Ok(new SessionResponse(
            new CurrentUserResponse(user.UserId, user.FullName, user.Email, user.RoleName, user.IspId, user.Permissions),
            result.SessionExpiresAt!.Value));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        IIdentityService identityService,
        IOptions<SessionOptions> sessionOptions,
        CancellationToken cancellationToken)
    {
        var cookieName = sessionOptions.Value.CookieName;

        if (httpContext.Request.Cookies.TryGetValue(cookieName, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            await identityService.SignOutAsync(token, cancellationToken).ConfigureAwait(false);
        }

        httpContext.Response.Cookies.Delete(cookieName, new CookieOptions { Path = "/" });

        return Results.NoContent();
    }

    private static IResult GetCurrentUser(ClaimsPrincipal user)
    {
        var userId = long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!, System.Globalization.CultureInfo.InvariantCulture);
        var ispIdClaim = user.FindFirstValue(BitstreamClaimTypes.IspId);
        var permissions = user.FindAll(BitstreamClaimTypes.Permission).Select(claim => claim.Value).ToArray();

        return Results.Ok(new CurrentUserResponse(
            userId,
            user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            ispIdClaim is null ? null : long.Parse(ispIdClaim, System.Globalization.CultureInfo.InvariantCulture),
            permissions));
    }
}
