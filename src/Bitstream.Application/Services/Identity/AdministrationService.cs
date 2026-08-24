using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Configuration;
using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Identity;
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
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<Role> _roleManager;
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
    private readonly IOptionsMonitor<PasswordPolicyOptions> _passwordPolicyOptions;

    public AdministrationService(
        IIspRepository ispRepository,
        IUserRepository userRepository,
        UserManager<User> userManager,
        RoleManager<Role> roleManager,
        IUserSessionStore sessionStore,
        IPasswordHasher passwordHasher,
        IPasswordPolicyValidator passwordPolicyValidator,
        ITotpService totpService,
        ITotpSecretProtector totpSecretProtector,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<TwoFactorOptions> twoFactorOptions,
        IOptionsMonitor<PasswordPolicyOptions> passwordPolicyOptions)
    {
        _ispRepository = ispRepository;
        _userRepository = userRepository;
        _userManager = userManager;
        _roleManager = roleManager;
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
        _passwordPolicyOptions = passwordPolicyOptions;
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

    public async Task<PagedResult<Isp>> SearchIspsAsync(string? search, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_currentUser.HasPermission(PermissionCodes.IspReadAll))
        {
            var (items, totalCount) = await _ispRepository.SearchAsync(search, skip, take, cancellationToken).ConfigureAwait(false);
            return new PagedResult<Isp>(items, totalCount);
        }

        // Not entitled to browse: the caller's own ISP is the entire result set, exactly what
        // GetIspAsync would return for the same ID — never an error, just a narrower list.
        if (_currentUser.IspId is not { } ownIspId)
        {
            return new PagedResult<Isp>([], 0);
        }

        var isp = await _ispRepository.FindByIdAsync(ownIspId, cancellationToken).ConfigureAwait(false);
        var matches = isp is not null && MatchesSearch(search, isp.Name, isp.Nipt);

        return matches ? new PagedResult<Isp>([isp!], 1) : new PagedResult<Isp>([], 0);
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
                lockedUserIds.Add(user.Id);
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

        if (violations.Count == 0 && await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false) is not null)
        {
            // TR-SEC-01: unique across the platform.
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

        var role = await ResolveRoleAsync(request.RoleName, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var user = new User
        {
            IspId = request.IspId,
            FullName = request.FullName,
            Email = request.Email,
            // This app has no separate username concept — UserName always equals Email, purely
            // because Identity's own UserValidator requires it to be non-empty.
            UserName = request.Email,
            Mobile = request.Mobile,
            RoleId = role.Id,
            // Set explicitly rather than left to EF's change-tracker fixup: this is the entity
            // CreateUserAsync hands back to the caller, and ToResponse (AdministrationEndpoints)
            // reads user.Role.Name from it before anything would trigger a reload.
            Role = role,
            Status = UserStatus.Active,
            // Overwritten by UserManager.CreateAsync below (via Argon2IdentityPasswordHasher) —
            // required only because the property itself is non-nullable.
            PasswordHash = string.Empty,
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

        var createResult = await _userManager.CreateAsync(user, request.InitialPassword).ConfigureAwait(false);

        if (!createResult.Succeeded)
        {
            throw new AdministrationValidationException([.. createResult.Errors.Select(error => error.Description)]);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // user.PasswordHash is now the real Argon2id hash — UserManager.CreateAsync set it via
        // Argon2IdentityPasswordHasher before the save above.
        await _userRepository.AddPasswordHistoryAsync(user.Id, user.PasswordHash!, _passwordHasher.AlgorithmTag, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "User.Created", "User", user.Id.ToString(CultureInfo.InvariantCulture),
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

        return await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
    }

    public async Task<PagedResult<User>> SearchUsersAsync(string? search, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_currentUser.HasPermission(PermissionCodes.IspReadAll))
        {
            var (items, totalCount) = await _userRepository.SearchAsync(search, ispId: null, skip, take, cancellationToken).ConfigureAwait(false);
            return new PagedResult<User>(items, totalCount);
        }

        // Same "no directory of teammates" rule as GetUserAsync: a non-privileged caller's
        // search can only ever find themselves, never another user at the same ISP.
        if (_currentUser.UserId is not { } ownUserId)
        {
            return new PagedResult<User>([], 0);
        }

        var user = await _userManager.FindByIdAsync(ownUserId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        var matches = user is not null && MatchesSearch(search, user.FullName, user.Email!);

        return matches ? new PagedResult<User>([user!], 1) : new PagedResult<User>([], 0);
    }

    public async Task SetUserStatusAsync(long userId, UserStatus status, CancellationToken cancellationToken = default)
    {
        if (status == UserStatus.Deleted)
        {
            // Deletion is its own action (DeleteUserAsync), with its own audit event and its own
            // idempotency rule — not a status this endpoint accepts, so a lock/unlock caller can
            // never reach it by accident and skip that path's guarantees.
            throw new AdministrationValidationException("Use the delete action to remove a user, not the status endpoint.");
        }

        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
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

    public async Task<User> UpdateUserAsync(long userId, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        var violations = new List<string>();

        RequireNonEmpty(request.FullName, "Full name", violations);
        RequireValidEmail(request.Email, "Email", violations);
        RequireValidE164(request.Mobile, "Mobile", violations);

        if (!SeededRoleNames.Contains(request.RoleName, StringComparer.Ordinal))
        {
            violations.Add($"Role '{request.RoleName}' is not a recognised role.");
        }

        if (violations.Count == 0)
        {
            var existing = await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false);

            if (existing is not null && existing.Id != userId)
            {
                // TR-SEC-01: unique across the platform.
                violations.Add($"A user with email '{request.Email}' already exists.");
            }
        }

        if (request.IspId is { } ispId && await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false) is null)
        {
            violations.Add($"ISP {ispId} does not exist.");
        }

        if (violations.Count > 0)
        {
            throw new AdministrationValidationException(violations);
        }

        var role = await ResolveRoleAsync(request.RoleName, cancellationToken).ConfigureAwait(false);

        var previous = $"{{\"fullName\":{JsonSerializer.Serialize(user.FullName)},\"email\":{JsonSerializer.Serialize(user.Email)},\"role\":{JsonSerializer.Serialize(user.Role.Name)}}}";

        user.FullName = request.FullName;
        user.Email = request.Email;
        user.UserName = request.Email;
        user.Mobile = request.Mobile;
        user.IspId = request.IspId;
        user.RoleId = role.Id;
        user.Role = role;

        var updateResult = await _userManager.UpdateAsync(user).ConfigureAwait(false);

        if (!updateResult.Succeeded)
        {
            throw new AdministrationValidationException([.. updateResult.Errors.Select(error => error.Description)]);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "User.Updated", "User", userId.ToString(CultureInfo.InvariantCulture),
            previous, $"{{\"fullName\":{JsonSerializer.Serialize(user.FullName)},\"email\":{JsonSerializer.Serialize(user.Email)},\"role\":{JsonSerializer.Serialize(request.RoleName)}}}",
            cancellationToken).ConfigureAwait(false);

        return user;
    }

    public async Task ChangeUserPasswordAsync(long userId, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newPassword);

        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        var recentHashes = await _userRepository.GetRecentPasswordHashesAsync(
            userId, _passwordPolicyOptions.CurrentValue.PasswordHistoryCount, cancellationToken).ConfigureAwait(false);

        var passwordCheck = _passwordPolicyValidator.Validate(newPassword, recentHashes);

        if (!passwordCheck.IsValid)
        {
            throw new AdministrationValidationException(passwordCheck.Violations);
        }

        // Set directly via the app's own Argon2id hasher (TR-SEC-02) rather than Identity's
        // token-based reset flow — this codebase does not implement IUserSecurityStampStore, and
        // an administrator resetting a password does not need the current one, so there is no
        // token to verify in the first place.
        user.PasswordHash = _passwordHasher.Hash(newPassword);
        user.PasswordHashAlgorithm = _passwordHasher.AlgorithmTag;
        user.PasswordUpdatedAt = _clock.UtcNow;

        var updateResult = await _userManager.UpdateAsync(user).ConfigureAwait(false);

        if (!updateResult.Succeeded)
        {
            throw new AdministrationValidationException([.. updateResult.Errors.Select(error => error.Description)]);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _userRepository.AddPasswordHistoryAsync(userId, user.PasswordHash!, _passwordHasher.AlgorithmTag, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // TR-SEC-07: a session opened under the old password must not outlive the change.
        await _sessionStore.RevokeAllForUserAsync(userId, "PasswordChanged", _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "User.PasswordChanged", "User", userId.ToString(CultureInfo.InvariantCulture),
            null, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        if (user.Status == UserStatus.Deleted)
        {
            return;
        }

        var previousStatus = user.Status;
        user.Status = UserStatus.Deleted;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // TR-SEC-07: a deleted user's sessions must not outlive the deletion.
        await _sessionStore.RevokeAllForUserAsync(userId, "UserDeleted", _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        await _auditWriter.WriteAsync(
            "User.Deleted", "User", userId.ToString(CultureInfo.InvariantCulture),
            $"{{\"status\":\"{previousStatus}\"}}", "{\"status\":\"Deleted\"}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when the caller may read the ISP: holds <c>isp.read.all</c>, or it is their own.
    /// Deliberately claims-only — no database round trip — so the not-found decision for an
    /// unauthorised request costs nothing before it is made (TR-SEC-19).
    /// </summary>
    private bool CanAccessIsp(long ispId) =>
        _currentUser.HasPermission(PermissionCodes.IspReadAll) || _currentUser.IspId == ispId;

    /// <summary>Same case-insensitive substring rule the repositories' own SearchAsync methods apply, for the single-record fallback a non-privileged caller's search reduces to.</summary>
    private static bool MatchesSearch(string? search, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        foreach (var field in fields)
        {
            if (field.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task LogCrossIspAttemptAsync(string entityType, long entityId, CancellationToken cancellationToken)
    {
        // TR-SEC-19: logged as a security event regardless of what is returned to the caller.
        await _auditWriter.WriteAsync(
            "Security.AccessDenied.CrossIsp", entityType, entityId.ToString(CultureInfo.InvariantCulture),
            null, $"{{\"callerIspId\":{(_currentUser.IspId is { } ispId ? ispId.ToString(CultureInfo.InvariantCulture) : "null")}}}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Role> ResolveRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        // Roles are seeded (db/mssql/0007) rather than looked up through a repository method of
        // their own: there are exactly four, they never change through this service, and
        // TR-SEC-21's configurability is about permission assignment, not the role list itself.
        var role = await _roleManager.FindByNameAsync(roleName).ConfigureAwait(false);

        return role ??
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
