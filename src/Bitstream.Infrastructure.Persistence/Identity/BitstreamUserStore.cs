using System.Globalization;
using Bitstream.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// Bridges <see cref="User"/> to <c>UserManager&lt;User&gt;</c> without adopting Identity's own
/// EF store or schema (ADR-0002: no EF migrations, ever — the schema stays hand-written T-SQL).
/// <see cref="User"/> itself is an ordinary Domain POCO, unchanged; nothing here requires it to
/// inherit <c>IdentityUser&lt;TKey&gt;</c>, since <c>IUserStore&lt;TUser&gt;</c> has no such
/// constraint.
/// <para>
/// Deliberately implements only the store interfaces this app actually uses:
/// <see cref="IUserPasswordStore{TUser}"/> and <see cref="IUserEmailStore{TUser}"/>. Not
/// <c>IUserLockoutStore</c> (lockout is <see cref="User.FailedLoginCount"/>/
/// <see cref="User.Status"/>, a business rule <c>IdentityService</c> already owns — TR-SEC-06)
/// and not <c>IUserSecurityStampStore</c> (session invalidation is the custom session store,
/// which the user explicitly chose to keep — TR-SEC-07). No new columns exist for either.
/// </para>
/// <para>
/// <see cref="CreateAsync"/>/<see cref="UpdateAsync"/>/<see cref="DeleteAsync"/> never call
/// <c>SaveChangesAsync</c> themselves — they only mutate the tracked <see cref="BitstreamDbContext"/>,
/// exactly like every other write in this codebase. The calling service still owns the
/// transaction via <c>IUnitOfWork.SaveChangesAsync</c> (e.g. <c>AdministrationService.CreateUserAsync</c>
/// also writes password history in the same commit) — this is what lets <c>UserManager</c> slot
/// into existing methods without changing their atomicity.
/// </para>
/// </summary>
public sealed class BitstreamUserStore : IUserStore<User>, IUserPasswordStore<User>, IUserEmailStore<User>
{
    private readonly BitstreamDbContext _dbContext;

    public BitstreamUserStore(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(user.UserId.ToString(CultureInfo.InvariantCulture));

    // No separate username concept in this app (TR-SEC-01: email is the unique identifier) —
    // UserName is always the email address.
    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email);

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken)
    {
        if (userName is not null)
        {
            user.Email = userName;
        }

        return Task.CompletedTask;
    }

    // Computed, not persisted: nothing needs a separate normalized-username column when
    // UserName and Email are the same value and Email is already looked up case-insensitively.
    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.ToUpperInvariant());

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken)
    {
        await _dbContext.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);

        return IdentityResult.Success;
    }

    public Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        _dbContext.Users.Update(user);

        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken)
    {
        // TR-DAT-07: no physical deletion anywhere in this schema. Nothing calls this today —
        // present only because IUserStore requires it.
        throw new NotSupportedException("Users are never physically deleted (TR-DAT-07). Use SetUserStatusAsync to lock instead.");
    }

    public Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        long.TryParse(userId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? WithGraph(_dbContext.Users).FirstOrDefaultAsync(user => user.UserId == id, cancellationToken)
            : Task.FromResult<User?>(null);

    // TR-SEC-01: unique across the platform, case-insensitively — matches how FindByEmailAsync
    // already worked before this store existed.
    public Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        WithGraph(_dbContext.Users).FirstOrDefaultAsync(
            user => user.Email.ToUpper() == normalizedUserName, cancellationToken);

    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.PasswordHash);

    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken)
    {
        if (passwordHash is not null)
        {
            user.PasswordHash = passwordHash;
        }

        return Task.CompletedTask;
    }

    // Every user in this app always has a password (no external logins, no passwordless flow).
    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email);

    public Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken)
    {
        if (email is not null)
        {
            user.Email = email;
        }

        return Task.CompletedTask;
    }

    // No email-confirmation flow in this app (TRD 4 has no self-registration) — every seeded or
    // administrator-created account is usable immediately.
    public Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        WithGraph(_dbContext.Users).FirstOrDefaultAsync(user => user.Email.ToUpper() == normalizedEmail, cancellationToken);

    public Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Email.ToUpperInvariant());

    public Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public void Dispose()
    {
        // BitstreamDbContext's lifetime is owned by DI (scoped), not by this store.
    }

    /// <summary>Role, its permissions, and the ISP — everything <c>AuthenticatedUser</c> and the RBAC checks need in one round trip.</summary>
    private static IQueryable<User> WithGraph(IQueryable<User> query) =>
        query
            .Include(user => user.Role)
                .ThenInclude(role => role.RolePermissions)
                    .ThenInclude(rolePermission => rolePermission.Permission)
            .Include(user => user.Isp);
}
