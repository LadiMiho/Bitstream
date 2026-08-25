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

    /// <param name="violations">Every message, flattened — <see cref="Exception.Message"/>/<see cref="Violations"/>, as before.</param>
    /// <param name="fieldErrors">The same messages, keyed by the request field each concerns (TR-NFR-12), so the presentation layer can show one next to the field it's about instead of a single combined banner.</param>
    public AdministrationValidationException(IReadOnlyList<string> violations, IReadOnlyDictionary<string, IReadOnlyList<string>> fieldErrors)
        : base(string.Join(" ", violations))
    {
        Violations = violations;
        FieldErrors = fieldErrors;
    }

    public IReadOnlyList<string> Violations { get; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<string>> FieldErrors { get; } = new Dictionary<string, IReadOnlyList<string>>();
}

/// <summary>Implements <see cref="IAdministrationService"/>: TRD 4.2, TR-SEC-09 to TR-SEC-16.</summary>
public sealed partial class AdministrationService : IAdministrationService
{
    /// <summary>
    /// Accumulates a request's validation violations both as a flat, human-readable list (what
    /// <see cref="AdministrationValidationException.Violations"/> has always been) and keyed by
    /// the request field each one concerns, so <see cref="ToException"/> can carry both.
    /// </summary>
    private sealed class ValidationCollector
    {
        private readonly List<string> _messages = [];
        private readonly Dictionary<string, List<string>> _fieldErrors = [];

        public int Count => _messages.Count;

        public void Add(string fieldKey, string message)
        {
            _messages.Add(message);

            if (!_fieldErrors.TryGetValue(fieldKey, out var fieldMessages))
            {
                fieldMessages = [];
                _fieldErrors[fieldKey] = fieldMessages;
            }

            fieldMessages.Add(message);
        }

        public void AddRange(string fieldKey, IEnumerable<string> messages)
        {
            foreach (var message in messages)
            {
                Add(fieldKey, message);
            }
        }

        public AdministrationValidationException ToException() =>
            new(_messages, _fieldErrors.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    private static readonly string[] SeededRoleNames = ["Administrator", "IspUser", "ServiceDesk", "Auditor"];

    private readonly IIspRepository _ispRepository;
    private readonly IUserRepository _userRepository;
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<Role> _roleManager;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IPasswordPolicyValidator _passwordPolicyValidator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly IClock _clock;
    private readonly ICurrentUserContext _currentUser;
    private readonly IOptionsMonitor<PasswordPolicyOptions> _passwordPolicyOptions;

    public AdministrationService(
        IIspRepository ispRepository,
        IUserRepository userRepository,
        UserManager<User> userManager,
        RoleManager<Role> roleManager,
        IPasswordHasher passwordHasher,
        IPasswordPolicyValidator passwordPolicyValidator,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        IClock clock,
        ICurrentUserContext currentUser,
        IOptionsMonitor<PasswordPolicyOptions> passwordPolicyOptions)
    {
        _ispRepository = ispRepository;
        _userRepository = userRepository;
        _userManager = userManager;
        _roleManager = roleManager;
        _passwordHasher = passwordHasher;
        _passwordPolicyValidator = passwordPolicyValidator;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _clock = clock;
        _currentUser = currentUser;
        _passwordPolicyOptions = passwordPolicyOptions;
    }

    public async Task<Isp> CreateIspAsync(CreateIspRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var violations = new ValidationCollector();

        // TR-SEC-15: ISP creation must validate and require these six fields.
        RequireNonEmpty(request.Name, "name", "Name", violations);
        RequireValidNipt(request.Nipt, violations);
        RequireNonEmpty(request.ContactPerson, "contactPerson", "Contact person", violations);
        RequireValidEmail(request.ContactEmail, "contactEmail", "Contact email", violations);
        RequireValidE164(request.ContactMobile, "contactMobile", "Contact mobile", violations);
        RequireNonEmpty(request.CrmBpReference, "crmBpReference", "CRM Business Partner reference", violations);

        if (violations.Count == 0 && await _ispRepository.NiptExistsAsync(request.Nipt, cancellationToken).ConfigureAwait(false))
        {
            // TR-SEC-16: NIPT unique across the platform.
            violations.Add("nipt", $"An ISP with NIPT '{request.Nipt}' already exists.");
        }

        if (violations.Count > 0)
        {
            throw violations.ToException();
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
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var lockedUserIds = new List<long>();

        if (status == IspStatus.Locked)
        {
            // TR-SEC-13: locking an ISP locks every one of its currently-active, not-already-locked
            // users. Unlocking the ISP does not reciprocally unlock them — see
            // IAdministrationService.SetIspStatusAsync. TR-SEC-07: UpdateSecurityStampAsync
            // invalidates each user's existing cookie immediately (checked every request —
            // Program.cs sets SecurityStampValidatorOptions.ValidationInterval to zero) rather
            // than leaving it to lapse at its next natural expiry.
            var users = await _userRepository.GetByIspIdAsync(ispId, cancellationToken).ConfigureAwait(false);

            foreach (var user in users.Where(u => u.Status == UserStatus.Active))
            {
                if (await _userManager.IsLockedOutAsync(user).ConfigureAwait(false))
                {
                    continue;
                }

                await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue).ConfigureAwait(false);
                await _userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
                lockedUserIds.Add(user.Id);
            }
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

        var violations = new ValidationCollector();

        // TR-SEC-14: full name, RFC-compliant unique email, E.164 mobile.
        RequireNonEmpty(request.FullName, "fullName", "Full name", violations);
        RequireValidEmail(request.Email, "email", "Email", violations);
        RequireValidE164(request.Mobile, "mobile", "Mobile", violations);

        if (!SeededRoleNames.Contains(request.RoleName, StringComparer.Ordinal))
        {
            violations.Add("roleName", $"Role '{request.RoleName}' is not a recognised role.");
        }

        if (violations.Count == 0 && await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false) is not null)
        {
            // TR-SEC-01: unique across the platform.
            violations.Add("email", $"A user with email '{request.Email}' already exists.");
        }

        Isp? isp = null;

        if (request.IspId is { } ispId)
        {
            isp = await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false);

            if (isp is null)
            {
                violations.Add("ispId", $"ISP {ispId} does not exist.");
            }
        }

        var recentHashes = Array.Empty<string>(); // no history yet for a new user
        var passwordCheck = _passwordPolicyValidator.Validate(request.InitialPassword, recentHashes);

        if (!passwordCheck.IsValid)
        {
            violations.AddRange("initialPassword", passwordCheck.Violations);
        }

        if (violations.Count > 0)
        {
            throw violations.ToException();
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

        var createResult = await _userManager.CreateAsync(user, request.InitialPassword).ConfigureAwait(false);

        if (!createResult.Succeeded)
        {
            throw new AdministrationValidationException([.. createResult.Errors.Select(error => error.Description)]);
        }

        // TR-SEC-04: every user goes through 2FA at every login, from their very first one — no
        // channel-specific pre-provisioning needed here. For the Totp channel, the authenticator
        // key itself is generated lazily on that first login (AuthEndpoints.LoginAsync), the
        // moment its absence is what signals "not yet enrolled."
        await _userManager.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);

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

        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

        if (user is not null)
        {
            // UserManager's own store never loads the Role navigation (Identity has no concept
            // of it — this app's single-role-per-user design, see BitstreamIdentityDbContext) —
            // every caller of GetUserAsync (the three drawer views, ToResponse) reads user.Role.
            user.Role = await _roleManager.FindByIdAsync(user.RoleId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
                throw new InvalidOperationException($"User {userId} references role {user.RoleId}, which does not exist.");
        }

        return user;
    }

    public async Task<PagedResult<User>> SearchUsersAsync(string? search, string? roleName, string? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_currentUser.HasPermission(PermissionCodes.IspReadAll))
        {
            var (items, totalCount) = await _userRepository.SearchAsync(search, ispId: null, roleName, status, skip, take, cancellationToken).ConfigureAwait(false);
            return new PagedResult<User>(items, totalCount);
        }

        // Same "no directory of teammates" rule as GetUserAsync: a non-privileged caller's
        // search can only ever find themselves, never another user at the same ISP.
        if (_currentUser.UserId is not { } ownUserId)
        {
            return new PagedResult<User>([], 0);
        }

        var user = await _userManager.FindByIdAsync(ownUserId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (user is not null)
        {
            user.Role = await _roleManager.FindByIdAsync(user.RoleId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        var isLockedOut = user is not null && await _userManager.IsLockedOutAsync(user).ConfigureAwait(false);
        var matchesStatus = string.IsNullOrEmpty(status) || string.Equals(status, isLockedOut ? "Locked" : "Active", StringComparison.Ordinal);
        var matchesRole = string.IsNullOrEmpty(roleName) || string.Equals(user?.Role?.Name, roleName, StringComparison.Ordinal);
        var matches = user is not null && matchesRole && matchesStatus && MatchesSearch(search, user.FullName, user.Email!);

        return matches ? new PagedResult<User>([user!], 1) : new PagedResult<User>([], 0);
    }

    public async Task SetUserLockedAsync(long userId, bool locked, CancellationToken cancellationToken = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        if (user.Status == UserStatus.Deleted)
        {
            // Deletion is its own, further-along state (DeleteUserAsync) — locking/unlocking a
            // deleted user is meaningless, so this is treated as "no such (active) user" rather
            // than silently flipping a lockout flag nobody can observe (the user cannot
            // authenticate either way).
            throw new AdministrationValidationException($"User {userId} does not exist.");
        }

        var wasLockedOut = await _userManager.IsLockedOutAsync(user).ConfigureAwait(false);

        if (wasLockedOut == locked)
        {
            return;
        }

        // TR-SEC-12: "locked" is not User.Status — it's UserManager's own LockoutEnd.
        await _userManager.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null).ConfigureAwait(false);

        if (locked)
        {
            // TR-SEC-07: invalidates the session immediately (checked every request — see
            // SetIspStatusAsync), not at its next natural expiry.
            await _userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);
        }

        await _auditWriter.WriteAsync(
            "User.StatusChanged", "User", userId.ToString(CultureInfo.InvariantCulture),
            $"{{\"locked\":{(wasLockedOut ? "true" : "false")}}}", $"{{\"locked\":{(locked ? "true" : "false")}}}",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<User> UpdateUserAsync(long userId, UpdateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var user = await _userManager.FindByIdAsync(userId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false) ??
            throw new AdministrationValidationException($"User {userId} does not exist.");

        var violations = new ValidationCollector();

        RequireNonEmpty(request.FullName, "fullName", "Full name", violations);
        RequireValidEmail(request.Email, "email", "Email", violations);
        RequireValidE164(request.Mobile, "mobile", "Mobile", violations);

        if (!SeededRoleNames.Contains(request.RoleName, StringComparer.Ordinal))
        {
            violations.Add("roleName", $"Role '{request.RoleName}' is not a recognised role.");
        }

        if (violations.Count == 0)
        {
            var existing = await _userManager.FindByEmailAsync(request.Email).ConfigureAwait(false);

            if (existing is not null && existing.Id != userId)
            {
                // TR-SEC-01: unique across the platform.
                violations.Add("email", $"A user with email '{request.Email}' already exists.");
            }
        }

        if (request.IspId is { } ispId && await _ispRepository.FindByIdAsync(ispId, cancellationToken).ConfigureAwait(false) is null)
        {
            violations.Add("ispId", $"ISP {ispId} does not exist.");
        }

        if (violations.Count > 0)
        {
            throw violations.ToException();
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
            throw new AdministrationValidationException(
                passwordCheck.Violations,
                new Dictionary<string, IReadOnlyList<string>> { ["newPassword"] = passwordCheck.Violations });
        }

        // Token-based reset (an administrator resetting a password does not need the current
        // one, so this is ResetPasswordAsync rather than ChangePasswordAsync) — the app's own
        // Argon2id hasher (TR-SEC-02) still does the actual hashing via the overridden
        // IPasswordHasher<User> registration, this just goes through Identity's own store/token
        // pipeline instead of setting PasswordHash by hand. ResetPasswordAsync rotates the
        // security stamp internally, which is what invalidates a session opened under the old
        // password (TR-SEC-07) — checked every request, see SetIspStatusAsync.
        var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
        var updateResult = await _userManager.ResetPasswordAsync(user, resetToken, newPassword).ConfigureAwait(false);

        if (!updateResult.Succeeded)
        {
            throw new AdministrationValidationException([.. updateResult.Errors.Select(error => error.Description)]);
        }

        user.PasswordHashAlgorithm = _passwordHasher.AlgorithmTag;
        user.PasswordUpdatedAt = _clock.UtcNow;
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _userRepository.AddPasswordHistoryAsync(userId, user.PasswordHash!, _passwordHasher.AlgorithmTag, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

        // TR-SEC-07: a deleted user's session must not outlive the deletion — invalidated
        // immediately (checked every request, see SetIspStatusAsync), not left to lapse.
        await _userManager.UpdateSecurityStampAsync(user).ConfigureAwait(false);

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

    private static void RequireNonEmpty(string value, string fieldKey, string fieldName, ValidationCollector violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(fieldKey, $"{fieldName} is required.");
        }
    }

    private static void RequireValidEmail(string value, string fieldKey, string fieldName, ValidationCollector violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(fieldKey, $"{fieldName} is required.");
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
            violations.Add(fieldKey, $"{fieldName} is not a valid email address.");
        }
    }

    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164Pattern();

    private static void RequireValidE164(string value, string fieldKey, string fieldName, ValidationCollector violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(fieldKey, $"{fieldName} is required.");
            return;
        }

        // E.164: a leading '+', no leading zero, 7 to 15 digits total (TR-SEC-14/15).
        if (!E164Pattern().IsMatch(value))
        {
            violations.Add(fieldKey, $"{fieldName} must be in E.164 format, e.g. +35569XXXXXXX.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9]{5,20}$")]
    private static partial Regex NiptPattern();

    private static void RequireValidNipt(string value, ValidationCollector violations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add("nipt", "NIPT is required.");
            return;
        }

        // Albanian NIPT: alphanumeric, typically 10 characters (e.g. L12345678A); validated
        // loosely (5-20 alphanumeric) since the TRD binds uniqueness and format but does not
        // specify a canonical checksum algorithm to verify against (TR-SEC-16).
        if (!NiptPattern().IsMatch(value))
        {
            violations.Add("nipt", "NIPT must be 5 to 20 alphanumeric characters.");
        }
    }
}
