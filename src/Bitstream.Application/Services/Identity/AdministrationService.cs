using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Thrown for a validated business rule violation (duplicate NIPT, unknown role, and so on).
/// The presentation layer maps this to 400/409/422 as appropriate; it is not an unexpected
/// failure, so it deliberately does not derive from an infrastructure exception type.
/// </summary>
public sealed class AdministrationValidationException : Exception
{
    public AdministrationValidationException(string message)
        : base(message)
    {
    }

    /// <param name="violations">One or more field-level messages (TR-NFR-12).</param>
    public AdministrationValidationException(IReadOnlyList<string> violations)
        : base(string.Join(" ", violations)) =>
        Violations = violations;

    public IReadOnlyList<string> Violations { get; } = [];
}

/// <summary>Implements <see cref="IAdministrationService"/>: TRD 4.2, TR-SEC-09 to TR-SEC-16.</summary>
public sealed partial class AdministrationService : IAdministrationService
{
    private static readonly string[] SeededRoleNames = ["Administrator", "IspUser", "ServiceDesk", "Auditor"];

    private readonly IIspRepository _ispRepository;
    private readonly IUserRepository _userRepository;
    private readonly IRoleRepository _roleRepository;
    private readonly IUserSessionStore _sessionStore;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPasswordPolicyValidator _passwordPolicyValidator;
    private readonly ITotpService _totpService;
    private readonly ITotpSecretProtector _totpSecretProtector;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<TwoFactorOptions> _twoFactorOptions;

    public AdministrationService(
        IIspRepository ispRepository,
        IUserRepository userRepository,
        IRoleRepository roleRepository,
        IUserSessionStore sessionStore,
        IPasswordHasher passwordHasher,
        IPasswordPolicyValidator passwordPolicyValidator,
        ITotpService totpService,
        ITotpSecretProtector totpSecretProtector,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<TwoFactorOptions> twoFactorOptions)
    {
        _ispRepository = ispRepository;
        _userRepository = userRepository;
        _roleRepository = roleRepository;
        _sessionStore = sessionStore;
        _passwordHasher = passwordHasher;
        _passwordPolicyValidator = passwordPolicyValidator;
        _totpService = totpService;
        _totpSecretProtector = totpSecretProtector;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _twoFactorOptions = twoFactorOptions;
    }

    public async Task<Isp> CreateIspAsync(CreateIspRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = new List<string>();

        // TR-SEC-15: ISP creation must validate and require these six fields.
        RequireNonEmpty(request.Name, "Name", violations);
        RequireValidNipt(request.Nipt, violations);
        RequireNonEmpty(request.ContactPerson, "Contact person", violations);
        RequireValidEmail(request.ContactEmail, "Contact email", violations);
        RequireValidE164(request.ContactMobile, "Contact mobile", violations);
        RequireNonEmpty(request.CrmBpReference, "CRM Business Partner reference", violations);

        if (violations.Count == 0 && await _ispRepository.NiptExistsAsync(request.Nipt, cancellationToken).ConfigureAwait(false))
        {
            // TR-SEC-16: NIPT unique across the platform.
            violations.Add($"An ISP with NIPT '{request.Nipt}' already exists.");
        }

        if (violations.Count > 0)
        {
            throw new AdministrationValidationException(violations);
        }

        var now = _clock.UtcNow;

        var isp = new Isp
        {
            Name = request.Name,
            Nipt = request.Nipt,
            ContactPerson = request.ContactPerson,
            ContactEmail = request.ContactEmail,
            ContactMobile = request.ContactMobile,
            CrmBpReference = request.CrmBpReference,
            Status = IspStatus.Active,
            CreatedAt = now,
            CreatedBy = _currentUser.UserId
        };

        await _ispRepository.AddAsync(isp, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "Isp.Created", "Isp", isp.IspId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"name\":{JsonSerializer.Serialize(isp.Name)},\"nipt\":{JsonSerializer.Serialize(isp.Nipt)}}}",
            cancellationToken).ConfigureAwait(false);

        return isp;
    }

    public async Task<Isp?> GetIspAsync(long ispId, CancellationToken cancellationToken = default)
    {
        // TR-SEC-19: a request for an ISP the caller does not own returns not-found, decided
        // purely from identity — never from whether the row exists — so that the response
        // cannot be used to confirm or deny another ISP's existence, and so an ISP that
        // genuinely does not exist behaves identically to one that does but is not the
        // caller's own.
        if (!CanAccessIsp(ispId))
        {
            await LogCrossIspAttemptAsync("Isp", ispId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetIspStatusAsync(long ispId, IspStatus status, CancellationToken cancellationToken = default)
    {
        var isp = await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"ISP {ispId} does not exist.");

        var previousStatus = isp.Status;

        if (previousStatus == status)
        {
            return;
        }

        isp.Status = status;

        var now = _clock.UtcNow;
        var lockedUserIds = new List<long>();

        if (status == IspStatus.Locked)
        {
            // TR-SEC-13: locking an ISP locks every one of its currently-active users. Unlocking
            // the ISP does not reciprocally unlock them — see IAdministrationService.SetIspStatusAsync.
            var users = await _userRepository.GetByIspIdAsync(ispId, cancellationToken).ConfigureAwait(false);

            foreach (var user in users.Where(u => u.Status == UserStatus.Active))
            {
                user.Status = UserStatus.Locked;
                lockedUserIds.Add(user.UserId);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // TR-SEC-07: revoked as a bulk operation, after the status change has committed, so a
        // session validated in between sees the new (locked) status and is refused anyway.
        if (status == IspStatus.Locked)
        {
            await _sessionStore.RevokeAllForIspAsync(ispId, "IspLocked", now, cancellationToken).ConfigureAwait(false);
        }

        await _auditWriter.WriteAsync(
            "Isp.StatusChanged", "Isp", ispId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}", $"{{\"status\":\"{status}\"}}",
            cancellationToken).ConfigureAwait(false);

        foreach (var userId in lockedUserIds)
        {
            await _auditWriter.WriteAsync(
                "User.StatusChanged", "User", userId.ToString(CultureInfo.InvariantCulture),
                "{\"status\":\"Active\"}", $"{{\"status\":\"Locked\",\"cause\":\"IspLocked\",\"ispId\":{ispId}}}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<User> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = new List<string>();

        // TR-SEC-14: full name, RFC-compliant unique email, E.164 mobile.
        RequireNonEmpty(request.FullName, "Full name", violations);
        RequireValidEmail(request.Email, "Email", violations);
        RequireValidE164(request.Mobile, "Mobile", violations);

        if (!SeededRoleNames.Contains(request.RoleName, StringComparer.Ordinal))
        {
            violations.Add($"Role '{request.RoleName}' is not a recognised role.");
        }

        if (violations.Count == 0 && await _userRepository.EmailExistsAsync(request.Email, cancellationToken).ConfigureAwait(false))
        {
            violations.Add($"A user with email '{request.Email}' already exists.");
        }

        Isp? isp = null;

        if (request.IspId is { } ispId)
        {
            isp = await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false);

            if (isp is null)
            {
                violations.Add($"ISP {ispId} does not exist.");
            }
        }

        var recentHashes = Array.Empty<string>(); // no history yet for a new user
        var passwordCheck = _passwordPolicyValidator.Validate(request.InitialPassword, recentHashes);

        if (!passwordCheck.IsValid)
        {
            violations.AddRange(passwordCheck.Violations);
        }

        if (violations.Count > 0)
        {
            throw new AdministrationValidationException(violations);
        }

        var now = _clock.UtcNow;
        var passwordHash = _passwordHasher.Hash(request.InitialPassword);

        var user = new User
        {
            IspId = request.IspId,
            FullName = request.FullName,
            Email = request.Email,
            Mobile = request.Mobile,
            RoleId = await ResolveRoleIdAsync(request.RoleName, cancellationToken).ConfigureAwait(false),
            Status = UserStatus.Active,
            PasswordHash = passwordHash,
            PasswordHashAlgorithm = _passwordHasher.AlgorithmTag,
            PasswordUpdatedAt = now,
            CreatedAt = now,
            CreatedBy = _currentUser.UserId
        };

        // TR-SEC-04/05: provision a TOTP secret up front when that is the configured channel, so
        // the very first login is not the moment provisioning is discovered to be missing (see
        // IdentityService.IssueChallengeAsync).
        if (_twoFactorOptions.CurrentValue.Channel == TwoFactorChannel.Totp)
        {
            var secret = _totpService.GenerateSecret();
            user.TotpSecret = await _totpSecretProtector.ProtectAsync(secret, cancellationToken).ConfigureAwait(false);
        }

        await _userRepository.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _userRepository.AddPasswordHistoryAsync(user.UserId, passwordHash, _passwordHasher.AlgorithmTag, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "User.Created", "User", user.UserId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"email\":{JsonSerializer.Serialize(user.Email)},\"role\":{JsonSerializer.Serialize(request.RoleName)},\"ispId\":{(request.IspId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "null")}}}",
            cancellationToken).ConfigureAwait(false);

        return user;
    }

    public async Task<User?> GetUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        // Self and Administrator/Auditor only (no directory of teammates in this module) —
        // same not-found-not-forbidden discipline as GetIspAsync (TR-SEC-19).
        if (!_currentUser.HasPermission(PermissionCodes.IspReadAll) && _currentUser.UserId != userId)
        {
            await LogCrossIspAttemptAsync("User", userId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return await _userRepository.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetUserStatusAsync(long userId, UserStatus status, CancellationToken cancellationToken = default)
    {
        var user = await _userRepository.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        var previousStatus = user.Status;

        if (previousStatus == status)
        {
            return;
        }

        user.Status = status;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (status == UserStatus.Locked)
        {
            // TR-SEC-07: locking invalidates the session immediately, not at its next natural expiry.
            await _sessionStore.RevokeAllForUserAsync(userId, "UserLocked", _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }

        await _auditWriter.WriteAsync(
            "User.StatusChanged", "User", userId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}", $"{{\"status\":\"{status}\"}}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the caller may read the ISP: holds <c>isp.read.all</c>, or it is their own.
    /// Deliberately claims-only — no database round trip — so the not-found decision for an
    /// unauthorised request costs nothing before it is made (TR-SEC-19).
    /// </summary>
    private bool CanAccessIsp(long ispId) =>
        _currentUser.HasPermission(PermissionCodes.IspReadAll) || _currentUser.IspId == ispId;

    private async Task LogCrossIspAttemptAsync(string entityType, long entityId, CancellationToken cancellationToken)
    {
        // TR-SEC-19: logged as a security event regardless of what is returned to the caller.
        await _auditWriter.WriteAsync(
            "Security.AccessDenied.CrossIsp", entityType, entityId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"callerIspId\":{(_currentUser.IspId is { } ispId ? ispId.ToString(CultureInfo.InvariantCulture) : "null")}}}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> ResolveRoleIdAsync(string roleName, CancellationToken cancellationToken)
    {
        // Roles are seeded (db/mssql/0007) rather than looked up through a repository method of
        // their own: there are exactly four, they never change through this service, and
        // TR-SEC-21's configurability is about permission assignment, not the role list itself.
        var role = await _roleRepository.FindByNameAsync(roleName, cancellationToken).ConfigureAwait(false);

        return role?.RoleId ??
            throw new AdministrationValidationException($"Role '{roleName}' is not seeded in this environment.");
    }

    private static void RequireNonEmpty(string value, string fieldName, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add($"{fieldName} is required.");
        }
    }

    private static void RequireValidEmail(string value, string fieldName, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add($"{fieldName} is required.");
            return;
        }

        try
        {
            // TR-SEC-14/15: RFC-compliant email. MailAddress is the pragmatic RFC 5322 check
            // the BCL offers; a regex-only check accepts or rejects strings a real mail system
            // would not agree with.
            _ = new MailAddress(value);
        }
        catch (FormatException)
        {
            violations.Add($"{fieldName} is not a valid email address.");
        }
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164Pattern();

    private static void RequireValidE164(string value, string fieldName, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add($"{fieldName} is required.");
            return;
        }

        // E.164: a leading '+', no leading zero, 7 to 15 digits total (TR-SEC-14/15).
        if (!E164Pattern().IsMatch(value))
        {
            violations.Add($"{fieldName} must be in E.164 format, e.g. +35569XXXXXXX.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{5,20}$")]
    private static partial Regex NiptPattern();

    private static void RequireValidNipt(string value, List<string> violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add("NIPT is required.");
            return;
        }

        // Albanian NIPT: alphanumeric, typically 10 characters (e.g. L12345678A); validated
        // loosely (5-20 alphanumeric) since the TRD binds uniqueness and format but does not
        // specify a canonical checksum algorithm to verify against (TR-SEC-16).
        if (!NiptPattern().IsMatch(value))
        {
            violations.Add("NIPT must be 5 to 20 alphanumeric characters.");
        }
    }
}
