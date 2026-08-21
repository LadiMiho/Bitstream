using System.Globalization;
using Bitstream.Application.Abstractions;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
// Microsoft.AspNetCore.Identity also declares a LockoutOptions — disambiguate in favour of ours.
using LockoutOptions = Bitstream.Application.Configuration.LockoutOptions;

namespace Bitstream.Application.Services.Identity;

/// <summary>Implements <see cref="IIdentityService"/>: TRD 4.1 authentication, TR-SEC-04 2FA, TR-SEC-07 sessions.</summary>
public sealed class IdentityService : IIdentityService
{
    private readonly UserManager<User> _userManager;
    private readonly ITwoFactorChallengeStore _challengeStore;
    private readonly IUserSessionStore _sessionStore;
    private readonly ITotpService _totpService;
    private readonly ITotpSecretProtector _totpSecretProtector;
    private readonly IEmailGateway _emailGateway;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICorrelationContext _correlationContext;
    private readonly IOptionsMonitor<LockoutOptions> _lockoutOptions;
    private readonly IOptionsMonitor<TwoFactorOptions> _twoFactorOptions;
    private readonly IOptionsMonitor<SessionOptions> _sessionOptions;
    private readonly ILogger<IdentityService> _logger;

    public IdentityService(
        UserManager<User> userManager,
        ITwoFactorChallengeStore challengeStore,
        IUserSessionStore sessionStore,
        ITotpService totpService,
        ITotpSecretProtector totpSecretProtector,
        IEmailGateway emailGateway,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICorrelationContext correlationContext,
        IOptionsMonitor<LockoutOptions> lockoutOptions,
        IOptionsMonitor<TwoFactorOptions> twoFactorOptions,
        IOptionsMonitor<SessionOptions> sessionOptions,
        ILogger<IdentityService> logger)
    {
        _userManager = userManager;
        _challengeStore = challengeStore;
        _sessionStore = sessionStore;
        _totpService = totpService;
        _totpSecretProtector = totpSecretProtector;
        _emailGateway = emailGateway;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _correlationContext = correlationContext;
        _lockoutOptions = lockoutOptions;
        _twoFactorOptions = twoFactorOptions;
        _sessionOptions = sessionOptions;
        _logger = logger;
    }

    public async Task<LoginResult> AuthenticateAsync(
        string email,
        string password,
        string? actorIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var user = await _userManager.FindByEmailAsync(email).ConfigureAwait(false);

        if (user is null)
        {
            // TR-SEC-22: every authentication attempt is recorded, including for an email that
            // does not exist — that pattern (many distinct unknown emails from one actor) is
            // itself a signal an administrator searching the audit log needs to see.
            await _auditWriter.WriteAsync(
                "Security.Login.Failed", "User", MaskEmail(email), null, "{\"reason\":\"NoSuchAccount\"}",
                cancellationToken).ConfigureAwait(false);

            return LoginResult.InvalidCredentials();
        }

        if (user.Status == UserStatus.Locked)
        {
            // TR-SEC-12: denied without checking the password at all.
            await _auditWriter.WriteAsync(
                "Security.Login.DeniedLocked", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
                null, null, cancellationToken).ConfigureAwait(false);

            return LoginResult.AccountLocked();
        }

        // TR-SEC-02: Argon2IdentityPasswordHasher also handles the opportunistic rehash under
        // the currently configured cost parameters — UserManager persists it on the tracked
        // entity (via BitstreamUserStore.UpdateAsync, which does not itself save), so it commits
        // in the same SaveChangesAsync as everything else below.
        if (!await _userManager.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            return await HandleFailedPasswordAsync(user, cancellationToken).ConfigureAwait(false);
        }

        if (user.FailedLoginCount != 0)
        {
            user.FailedLoginCount = 0;
        }

        var (challengeToken, channel, expiresAt, provisioningUri) = await IssueChallengeAsync(user, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "Security.Login.PasswordVerified", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);

        return LoginResult.ChallengeIssued(challengeToken, channel, expiresAt, provisioningUri);
    }

    public async Task<TwoFactorResult> CompleteSecondFactorAsync(
        string challengeToken,
        string code,
        string? actorIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var challenge = await _challengeStore.FindByTokenAsync(challengeToken, cancellationToken).ConfigureAwait(false);

        // No such challenge and "wrong code" are indistinguishable to the caller, for the same
        // enumeration reason as LoginOutcome.InvalidCredentials.
        if (challenge is null)
        {
            return TwoFactorResult.InvalidCode();
        }

        var now = _clock.UtcNow;

        if (challenge.ConsumedAt is not null)
        {
            // TR-SEC-04: "usable once." A consumed challenge is dead even if still within its
            // validity window.
            return TwoFactorResult.InvalidCode();
        }

        if (now >= challenge.ExpiresAt)
        {
            await _auditWriter.WriteAsync(
                "Security.TwoFactor.Expired", "User", challenge.UserId.ToString(CultureInfo.InvariantCulture),
                null, null, cancellationToken).ConfigureAwait(false);

            return TwoFactorResult.Expired();
        }

        var maxAttempts = _twoFactorOptions.CurrentValue.MaxVerificationAttempts;

        if (challenge.AttemptCount >= maxAttempts)
        {
            return TwoFactorResult.TooManyAttempts();
        }

        var user = challenge.User;
        var codeIsValid = await VerifyCodeAsync(challenge, user, code, cancellationToken).ConfigureAwait(false);

        if (!codeIsValid)
        {
            challenge.AttemptCount++;
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await _auditWriter.WriteAsync(
                "Security.TwoFactor.Failed", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
                null, $"{{\"attempt\":{challenge.AttemptCount}}}", cancellationToken).ConfigureAwait(false);

            return challenge.AttemptCount >= maxAttempts ? TwoFactorResult.TooManyAttempts() : TwoFactorResult.InvalidCode();
        }

        if (challenge.Channel == TwoFactorChannel.Totp && user.TotpConfirmedAt is null)
        {
            // The very code that just verified proves the secret reached an authenticator app;
            // this is what turns the QR-code enrollment screen off for every login after this one.
            user.TotpConfirmedAt = now;

            await _auditWriter.WriteAsync(
                "Security.TwoFactor.Enrolled", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
                null, null, cancellationToken).ConfigureAwait(false);
        }

        challenge.ConsumedAt = now;
        user.LastLoginAt = now;

        var rawToken = TokenHashing.GenerateOpaqueToken();
        var sessionOptions = _sessionOptions.CurrentValue;
        var expiresAt = now + sessionOptions.AbsoluteTimeout;

        var session = new UserSession
        {
            UserId = user.UserId,
            TokenHash = TokenHashing.Sha256Hex(rawToken),
            IssuedAt = now,
            ExpiresAt = expiresAt,
            LastActivityAt = now,
            IssuedFromIp = actorIp
        };

        await _sessionStore.AddAsync(session, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "Security.Login.Succeeded", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);

        return TwoFactorResult.Succeeded(rawToken, expiresAt, BuildAuthenticatedUser(user));
    }

    public async Task SignOutAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);

        var tokenHash = TokenHashing.Sha256Hex(sessionToken);
        var session = await _sessionStore.FindByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);

        if (session is null || session.RevokedAt is not null)
        {
            // Idempotent: signing out twice, or signing out a session already revoked by a lock
            // elsewhere, is not an error.
            return;
        }

        session.RevokedAt = _clock.UtcNow;
        session.RevokedReason = "UserSignedOut";

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "Security.Logout", "User", session.UserId.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthenticatedUser?> ValidateSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);

        var tokenHash = TokenHashing.Sha256Hex(sessionToken);
        var session = await _sessionStore.FindByTokenHashAsync(tokenHash, cancellationToken).ConfigureAwait(false);

        if (session is null || session.RevokedAt is not null)
        {
            return null;
        }

        var now = _clock.UtcNow;

        // TR-SEC-07: whichever limit is reached first.
        if (now >= session.ExpiresAt)
        {
            return null;
        }

        var idleTimeout = _sessionOptions.CurrentValue.IdleTimeout;

        if (now - session.LastActivityAt > idleTimeout)
        {
            // Revoked outright rather than left to lapse again next time: an idle-expired
            // session is exactly as dead as a logged-out one, and marking it lets an
            // administrator searching sessions see why it ended.
            session.RevokedAt = now;
            session.RevokedReason = "IdleTimeout";
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return null;
        }

        var user = session.User;

        if (user.Status != UserStatus.Active)
        {
            // Defence in depth. Locking a user or their ISP already revokes their sessions
            // (AdministrationService), so this should not normally trigger — it exists for the
            // narrow window between the lock being written and the revoke being written, and
            // for any session created through a path that predates that invariant.
            return null;
        }

        session.LastActivityAt = now;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return BuildAuthenticatedUser(user);
    }

    private async Task<LoginResult> HandleFailedPasswordAsync(User user, CancellationToken cancellationToken)
    {
        user.FailedLoginCount++;

        var maxAttempts = _lockoutOptions.CurrentValue.MaxFailedAttempts;
        var lockedThisAttempt = user.FailedLoginCount >= maxAttempts;
        var userIdText = user.UserId.ToString(CultureInfo.InvariantCulture);

        if (lockedThisAttempt)
        {
            user.Status = UserStatus.Locked;

            // TR-SEC-06: "an alert raised to the Administrator." The notification subsystem
            // (TRD 8) is not built yet — see docs/open-items.md — so the alert is a structured,
            // Warning-level log entry, which TR-NFR-16's centralised logging and alerting picks
            // up in the meantime. Swap this for INotificationService once TRD 8 exists.
            _logger.LogWarning(
                "Account {UserId} locked after {FailedCount} consecutive failed login attempts.",
                user.UserId, user.FailedLoginCount);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "Security.Login.Failed", "User", userIdText, null,
            $"{{\"reason\":\"InvalidPassword\",\"failedCount\":{user.FailedLoginCount},\"locked\":{(lockedThisAttempt ? "true" : "false")}}}",
            cancellationToken).ConfigureAwait(false);

        if (lockedThisAttempt)
        {
            await _auditWriter.WriteAsync(
                "Security.Account.AutoLocked", "User", userIdText, "{\"status\":\"Active\"}", "{\"status\":\"Locked\"}",
                cancellationToken).ConfigureAwait(false);
        }

        return lockedThisAttempt ? LoginResult.AccountLocked() : LoginResult.InvalidCredentials();
    }

    private async Task<(string Token, TwoFactorChannel Channel, DateTimeOffset ExpiresAt, string? ProvisioningUri)> IssueChallengeAsync(
        User user,
        CancellationToken cancellationToken)
    {
        var options = _twoFactorOptions.CurrentValue;
        var now = _clock.UtcNow;
        var token = TokenHashing.GenerateOpaqueToken();
        string? codeHash = null;
        string? provisioningUri = null;

        switch (options.Channel)
        {
            case TwoFactorChannel.Totp:
                if (user.TotpSecret is null)
                {
                    // TR-SEC-05 forbids falling back to a weaker channel, so a missing secret is
                    // a provisioning defect to fix, not a login failure to paper over.
                    throw new InvalidOperationException(
                        $"User {user.UserId} has no TOTP secret provisioned, but the configured " +
                        "second-factor channel is Totp. Re-create the user, or reconfigure " +
                        "Security:TwoFactor:Channel.");
                }

                if (user.TotpConfirmedAt is null)
                {
                    // Never confirmed a code: the secret exists, but nothing has scanned it yet.
                    // Decrypting it here (rather than only at verify time) is what lets the
                    // presentation layer show a QR code alongside the code prompt.
                    var secret = await _totpSecretProtector.UnprotectAsync(user.TotpSecret, cancellationToken).ConfigureAwait(false);
                    provisioningUri = _totpService.BuildProvisioningUri(secret, user.Email, "Bitstream Portal");
                }

                break;

            case TwoFactorChannel.EmailOtp:
                var code = TokenHashing.GenerateNumericCode(options.CodeLength);
                codeHash = TokenHashing.Sha256Hex(code);
                await SendEmailOtpAsync(user, code, cancellationToken).ConfigureAwait(false);
                break;

            case TwoFactorChannel.SmsOtp:
                // TRD 11.4 open item 13: the production 2FA channel is not decided, and no SMS
                // provider is named anywhere in the TRD. Consistent with the other adapters this
                // scaffold has left deliberately unimplemented (CrmHttpGateway, SapGateway).
                throw new NotSupportedException(
                    "SMS OTP is not implemented (TRD 11.4 open item 13 names no SMS provider). " +
                    "Configure Security:TwoFactor:Channel to Totp or EmailOtp.");

            default:
                throw new NotSupportedException($"Unknown two-factor channel '{options.Channel}'.");
        }

        var expiresAt = now + options.CodeValidity;

        var challenge = new TwoFactorChallenge
        {
            ChallengeToken = token,
            UserId = user.UserId,
            Channel = options.Channel,
            CodeHash = codeHash,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        await _challengeStore.AddAsync(challenge, cancellationToken).ConfigureAwait(false);

        return (token, options.Channel, expiresAt, provisioningUri);
    }

    private async Task<bool> VerifyCodeAsync(TwoFactorChallenge challenge, User user, string code, CancellationToken cancellationToken)
    {
        switch (challenge.Channel)
        {
            case TwoFactorChannel.Totp:
                if (user.TotpSecret is null)
                {
                    return false;
                }

                var secret = await _totpSecretProtector.UnprotectAsync(user.TotpSecret, cancellationToken).ConfigureAwait(false);
                return _totpService.Validate(secret, code);

            case TwoFactorChannel.EmailOtp:
            case TwoFactorChannel.SmsOtp:
                return challenge.CodeHash is not null &&
                       string.Equals(TokenHashing.Sha256Hex(code.Trim()), challenge.CodeHash, StringComparison.Ordinal);

            default:
                return false;
        }
    }

    private async Task SendEmailOtpAsync(User user, string code, CancellationToken cancellationToken)
    {
        // TR-ARC-03 requires the durable, business-critical outbound interfaces to go through
        // the outbox so a CRM or BI outage delays rather than loses them. A one-time code is the
        // opposite case: it is meaningless after its 5-minute validity, so queuing it for a
        // later retry would deliver a dead code. If the relay is unreachable right now, the
        // right outcome is to fail this login attempt, not to queue the message.
        var envelope = new IntegrationEnvelope(
            MessageId: Guid.NewGuid(),
            CorrelationId: _correlationContext.CorrelationId,
            IdempotencyKey: $"2fa-{user.UserId}-{_clock.UtcNow.Ticks}",
            OccurredAt: _clock.UtcNow);

        var message = new EmailMessage(
            envelope,
            To: [user.Email],
            Cc: [],
            Subject: "Bitstream Portal — your verification code",
            HtmlBody: $"<p>Your Bitstream Portal verification code is <strong>{code}</strong>. It expires in {_twoFactorOptions.CurrentValue.CodeValidity.TotalMinutes:F0} minutes and can only be used once.</p>",
            PlainTextBody: $"Your Bitstream Portal verification code is {code}. It expires in {_twoFactorOptions.CurrentValue.CodeValidity.TotalMinutes:F0} minutes and can only be used once.");

        var result = await _emailGateway.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new TwoFactorDeliveryException(
                $"Could not send the verification code to user {user.UserId}: {result.ErrorMessage}");
        }
    }

    private static AuthenticatedUser BuildAuthenticatedUser(User user) =>
        new(
            user.UserId,
            user.FullName,
            user.Email,
            user.Role.Name,
            user.IspId,
            [.. user.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code)]);

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
}
