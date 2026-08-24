using System.Globalization;
using System.Security.Claims;
using Bitstream.Application.Abstractions;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Hosting.Security;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Web.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace Bitstream.Web.Endpoints;

/// <summary>
/// TRD 4.1: authentication and 2FA, entirely ASP.NET Core Identity's own <c>SignInManager&lt;User&gt;</c>
/// now — password check, lockout (TR-SEC-06/12) and second-factor verification (TR-SEC-04) all run
/// through it, not a custom orchestration. Two calls per login (first factor, then second), plus
/// logout and a "who am I" endpoint the frontend uses to decide what to render — a decision
/// TR-SEC-20 requires is never trusted on its own, since every subsequent call is authorised
/// again server-side regardless of what the interface shows.
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
                "without checking the password. On success, a second factor is required and " +
                "must be completed at POST /login/verify.")
            .Accepts<LoginRequest>("application/json")
            .Produces<TwoFactorRequiredResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status423Locked)
            .AllowAnonymous();

        group.MapPost("/login/verify", VerifyTwoFactorAsync)
            .WithName("VerifyTwoFactor")
            .WithSummary("Second factor: TOTP or one-time code")
            .WithDescription(
                "TR-SEC-04. On success, sets the session as an HttpOnly cookie (TR-SEC-07) and " +
                "returns the caller's profile.")
            .Accepts<TwoFactorVerifyRequest>("application/json")
            .Produces<SessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .AllowAnonymous();

        group.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Invalidate the current session")
            .WithDescription("TR-SEC-07: the session is invalidated immediately, not merely forgotten by the client. Idempotent.")
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
        UserManager<User> userManager,
        SignInManager<User> signInManager,
        IAuditWriter auditWriter,
        IEmailGateway emailGateway,
        ICorrelationContext correlationContext,
        IClock clock,
        IOptionsMonitor<TwoFactorOptions> twoFactorOptions,
        IOptionsMonitor<Bitstream.Application.Configuration.SessionOptions> sessionOptions,
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

        var user = await userManager.FindByEmailAsync(request.Email).ConfigureAwait(false);

        if (user is null)
        {
            // TR-SEC-22: every authentication attempt is recorded, including for an email that
            // does not exist — that pattern (many distinct unknown emails from one actor) is
            // itself a signal an administrator searching the audit log needs to see.
            await auditWriter.WriteAsync(
                "Security.Login.Failed", "User", MaskEmail(request.Email), null, "{\"reason\":\"NoSuchAccount\"}",
                cancellationToken).ConfigureAwait(false);

            return InvalidCredentialsProblem();
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user).ConfigureAwait(false);

        if (wasLockedOut)
        {
            // TR-SEC-12: denied without checking the password at all.
            await auditWriter.WriteAsync(
                "Security.Login.DeniedLocked", "User", user.Id.ToString(CultureInfo.InvariantCulture),
                null, null, cancellationToken).ConfigureAwait(false);

            return AccountLockedProblem();
        }

        // TR-SEC-02: Argon2IdentityPasswordHasher (the overridden IPasswordHasher<User>) also
        // handles the opportunistic rehash — SignInManager persists that itself, via Identity's
        // own store. lockoutOnFailure: true is TR-SEC-06 (5 consecutive failed attempts, see
        // AddIdentity's Lockout options in Bitstream.Infrastructure.Persistence.DependencyInjection).
        var result = await signInManager.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true)
            .ConfigureAwait(false);

        if (result.IsLockedOut)
        {
            await auditWriter.WriteAsync(
                "Security.Login.Failed", "User", user.Id.ToString(CultureInfo.InvariantCulture), null,
                "{\"reason\":\"InvalidPassword\",\"locked\":true}", cancellationToken).ConfigureAwait(false);

            // TR-SEC-06: "an alert raised to the Administrator." The notification subsystem
            // (TRD 8) is not built yet — see docs/open-items.md — so the alert is a structured,
            // Warning-level log entry, which TR-NFR-16's centralised logging and alerting picks
            // up in the meantime.
            logger.LogWarning("Account {UserId} locked after too many consecutive failed login attempts.", user.Id);

            await auditWriter.WriteAsync(
                "Security.Account.AutoLocked", "User", user.Id.ToString(CultureInfo.InvariantCulture),
                "{\"locked\":false}", "{\"locked\":true}", cancellationToken).ConfigureAwait(false);

            return AccountLockedProblem();
        }

        if (!result.Succeeded && !result.RequiresTwoFactor)
        {
            await auditWriter.WriteAsync(
                "Security.Login.Failed", "User", user.Id.ToString(CultureInfo.InvariantCulture), null,
                "{\"reason\":\"InvalidPassword\",\"locked\":false}", cancellationToken).ConfigureAwait(false);

            return InvalidCredentialsProblem();
        }

        await auditWriter.WriteAsync(
            "Security.Login.PasswordVerified", "User", user.Id.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            // Every user has TwoFactorEnabled = true (AdministrationService.CreateUserAsync) —
            // this branch is not the normal path, but PasswordSignInAsync already completed the
            // full sign-in (cookie set) if it is somehow reached, so just report success.
            return Results.Ok(new SessionResponse(
                await BuildCurrentUserResponseAsync(user, httpContext, cancellationToken).ConfigureAwait(false),
                clock.UtcNow + sessionOptions.CurrentValue.IdleTimeout));
        }

        var channel = twoFactorOptions.CurrentValue.Channel;
        string? provisioningUri = null;

        switch (channel)
        {
            case TwoFactorChannel.Totp:
                var authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);

                if (authenticatorKey is null)
                {
                    // Never confirmed a code: nothing has scanned a key yet, because none exists.
                    // Generating it here (rather than only at verify time) is what lets the QR
                    // code render alongside the code prompt on this, this user's first login.
                    await userManager.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
                    authenticatorKey = await userManager.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
                    provisioningUri = BuildAuthenticatorProvisioningUri(authenticatorKey!, user.Email!);
                }

                break;

            case TwoFactorChannel.EmailOtp:
                var code = await userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider).ConfigureAwait(false);

                try
                {
                    await SendEmailOtpAsync(user, code, emailGateway, correlationContext, clock, cancellationToken).ConfigureAwait(false);
                }
                catch (TwoFactorDeliveryException exception)
                {
                    // The detail (which can name the failing relay and its raw error) is logged,
                    // never returned: this endpoint is anonymous, and a login failure response is
                    // not the place to disclose infrastructure detail (TR-SEC-27).
                    logger.LogError(exception, "Second-factor code could not be dispatched for {Email}.", request.Email);

                    // Distinguished from an unrelated server fault: the password was correct, but
                    // the second factor could not be dispatched right now — a 503 tells the
                    // caller to retry rather than that something in the portal is broken.
                    return Results.Problem(
                        title: "Could not send verification code",
                        detail: "The password was correct, but the verification code could not be sent. Please try again shortly.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                break;

            case TwoFactorChannel.SmsOtp:
                // TRD 11.4 open item 13: the production 2FA channel is not decided, and no SMS
                // provider is named anywhere in the TRD. Consistent with the other adapters this
                // scaffold has left deliberately unimplemented (CrmHttpGateway, SapGateway).
                throw new NotSupportedException(
                    "SMS OTP is not implemented (TRD 11.4 open item 13 names no SMS provider). " +
                    "Configure Security:TwoFactor:Channel to Totp or EmailOtp.");

            default:
                throw new NotSupportedException($"Unknown two-factor channel '{channel}'.");
        }

        return Results.Ok(new TwoFactorRequiredResponse(channel.ToString(), provisioningUri is null ? null : BuildQrCodeDataUri(provisioningUri)));
    }

    private static async Task<IResult> VerifyTwoFactorAsync(
        [FromBody] TwoFactorVerifyRequest request,
        HttpContext httpContext,
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IAuditWriter auditWriter,
        IClock clock,
        IOptionsMonitor<TwoFactorOptions> twoFactorOptions,
        IOptionsMonitor<Bitstream.Application.Configuration.SessionOptions> sessionOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.Problem(
                title: "Invalid request",
                detail: "code is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Tracks which account is mid-two-factor via Identity's own short-lived cookie, set by
        // PasswordSignInAsync's RequiresTwoFactor outcome — no challenge token to look up.
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync().ConfigureAwait(false);

        if (user is null)
        {
            return Results.Problem(
                title: "Verification failed",
                detail: "This sign-in attempt has expired. Start again.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var wasLockedOut = await userManager.IsLockedOutAsync(user).ConfigureAwait(false);
        var code = request.Code.Replace(" ", string.Empty, StringComparison.Ordinal);

        var result = twoFactorOptions.CurrentValue.Channel switch
        {
            TwoFactorChannel.Totp => await signInManager.TwoFactorAuthenticatorSignInAsync(code, isPersistent: false, rememberClient: false).ConfigureAwait(false),
            TwoFactorChannel.EmailOtp => await signInManager.TwoFactorSignInAsync(TokenOptions.DefaultEmailProvider, code, isPersistent: false, rememberClient: false).ConfigureAwait(false),
            _ => Microsoft.AspNetCore.Identity.SignInResult.Failed
        };

        if (!result.Succeeded)
        {
            if (result.IsLockedOut)
            {
                await auditWriter.WriteAsync(
                    "Security.TwoFactor.Failed", "User", user.Id.ToString(CultureInfo.InvariantCulture),
                    null, "{\"locked\":true}", cancellationToken).ConfigureAwait(false);

                if (!wasLockedOut)
                {
                    await auditWriter.WriteAsync(
                        "Security.Account.AutoLocked", "User", user.Id.ToString(CultureInfo.InvariantCulture),
                        "{\"locked\":false}", "{\"locked\":true}", cancellationToken).ConfigureAwait(false);
                }

                return Results.Problem(
                    title: "Account locked",
                    detail: "This account is locked after too many failed sign-in attempts. Contact your administrator.",
                    statusCode: StatusCodes.Status423Locked);
            }

            await auditWriter.WriteAsync(
                "Security.TwoFactor.Failed", "User", user.Id.ToString(CultureInfo.InvariantCulture),
                null, "{\"locked\":false}", cancellationToken).ConfigureAwait(false);

            return Results.Problem(title: "Verification failed", detail: "The code is incorrect.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await auditWriter.WriteAsync(
            "Security.Login.Succeeded", "User", user.Id.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);

        user.LastLoginAt = clock.UtcNow;
        await userManager.UpdateAsync(user).ConfigureAwait(false);

        return Results.Ok(new SessionResponse(
            await BuildCurrentUserResponseAsync(user, httpContext, cancellationToken).ConfigureAwait(false),
            clock.UtcNow + sessionOptions.CurrentValue.IdleTimeout));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SignInManager<User> signInManager,
        UserManager<User> userManager,
        IAuditWriter auditWriter,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

        await signInManager.SignOutAsync().ConfigureAwait(false);

        if (userId is not null)
        {
            // TR-SEC-07: invalidates immediately, not merely the browser's own copy of the
            // cookie — a stolen cookie stops working the moment this security stamp rotates
            // (checked every request, SecurityStampValidatorOptions.ValidationInterval is zero).
            var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false);

            if (user is not null)
            {
                await userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);

                await auditWriter.WriteAsync(
                    "Security.Logout", "User", userId, null, null, cancellationToken).ConfigureAwait(false);
            }
        }

        return Results.NoContent();
    }

    private static IResult GetCurrentUser(ClaimsPrincipal user)
    {
        var userId = long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!, CultureInfo.InvariantCulture);
        var ispIdClaim = user.FindFirstValue(BitstreamClaimTypes.IspId);
        var permissions = user.FindAll(BitstreamClaimTypes.Permission).Select(claim => claim.Value).ToArray();

        return Results.Ok(new CurrentUserResponse(
            userId,
            user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            ispIdClaim is null ? null : long.Parse(ispIdClaim, CultureInfo.InvariantCulture),
            permissions));
    }

    private static async Task<CurrentUserResponse> BuildCurrentUserResponseAsync(User user, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var dbContext = httpContext.RequestServices.GetRequiredService<BitstreamDbContext>();

        var role = await dbContext.Roles
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .AsNoTracking()
            .FirstAsync(r => r.Id == user.RoleId, cancellationToken)
            .ConfigureAwait(false);

        return new CurrentUserResponse(
            user.Id,
            user.FullName,
            user.Email!,
            role.Name!,
            user.IspId,
            [.. role.RolePermissions.Select(rp => rp.Permission.Code)]);
    }

    private static async Task SendEmailOtpAsync(
        User user, string code, IEmailGateway emailGateway, ICorrelationContext correlationContext, IClock clock, CancellationToken cancellationToken)
    {
        // TR-ARC-03 requires the durable, business-critical outbound interfaces to go through
        // the outbox so a CRM or BI outage delays rather than loses them. A one-time code is the
        // opposite case: it is meaningless a few minutes from now, so queuing it for a later
        // retry would deliver a dead code. If the relay is unreachable right now, the right
        // outcome is to fail this login attempt, not to queue the message.
        var envelope = new IntegrationEnvelope(
            MessageId: Guid.NewGuid(),
            CorrelationId: correlationContext.CorrelationId,
            IdempotencyKey: $"2fa-{user.Id}-{clock.UtcNow.Ticks}",
            OccurredAt: clock.UtcNow);

        var message = new EmailMessage(
            envelope,
            To: [user.Email!],
            Cc: [],
            Subject: "Bitstream Portal — your verification code",
            HtmlBody: $"<p>Your Bitstream Portal verification code is <strong>{code}</strong>. It can only be used once.</p>",
            PlainTextBody: $"Your Bitstream Portal verification code is {code}. It can only be used once.");

        var result = await emailGateway.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new TwoFactorDeliveryException(
                $"Could not send the verification code to user {user.Id}: {result.ErrorMessage}");
        }
    }

    private static IResult InvalidCredentialsProblem() =>
        Results.Problem(
            title: "Invalid credentials",
            detail: "The email or password is incorrect.",
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult AccountLockedProblem() =>
        Results.Problem(
            title: "Account locked",
            // TR-NFR-12: specific and actionable. Disclosed deliberately — a closed wholesale
            // portal with no public self-registration, where a legitimate locked-out user needs
            // to know why (TR-SEC-06).
            detail: "This account is locked after too many failed sign-in attempts. Contact your administrator.",
            statusCode: StatusCodes.Status423Locked);

    /// <summary>
    /// Keeps the local part's first character and the domain, e.g. <c>j***@example.com</c>, for
    /// the audit entry of a login attempt against an email that has no matching account — enough
    /// to spot a pattern without writing the full address for an account that might not even
    /// exist.
    /// </summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);

        return at <= 1 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary>
    /// <c>otpauth://</c> URI for an authenticator app — a standards-format string, not TOTP logic
    /// (Identity's own <c>AuthenticatorTokenProvider&lt;User&gt;</c> does the actual RFC 6238
    /// generation/validation server-side); ASP.NET Core Identity has no built-in helper for this,
    /// so building the URI stays presentation-layer glue, same as before this migration.
    /// </summary>
    private static string BuildAuthenticatorProvisioningUri(string authenticatorKey, string email) =>
        $"otpauth://totp/{Uri.EscapeDataString("Bitstream Portal")}:{Uri.EscapeDataString(email)}" +
        $"?secret={authenticatorKey}&issuer={Uri.EscapeDataString("Bitstream Portal")}&digits=6";

    /// <summary>
    /// Rendering an <c>otpauth://</c> URI as an image is a presentation concern, not a business
    /// decision — that decision (whether this login needs enrollment at all) is
    /// <see cref="BuildAuthenticatorProvisioningUri"/>'s caller's. Null in, null out: most logins
    /// have nothing to render.
    /// </summary>
    private static string? BuildQrCodeDataUri(string? provisioningUri)
    {
        if (provisioningUri is null)
        {
            return null;
        }

        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(provisioningUri, QRCodeGenerator.ECCLevel.M);
        var pngQrCode = new PngByteQRCode(qrData);
        var pngBytes = pngQrCode.GetGraphic(8);

        return $"data:image/png;base64,{Convert.ToBase64String(pngBytes)}";
    }
}
