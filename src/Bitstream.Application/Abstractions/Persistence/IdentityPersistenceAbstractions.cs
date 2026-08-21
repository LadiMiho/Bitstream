using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>
/// Everything about a user that <c>UserManager&lt;User&gt;</c> does not cover, because it isn't
/// part of Identity's contract: browsing/searching (TRD 4.2), cascading a lock across an ISP's
/// users (TR-SEC-13), and the password-reuse history (TR-SEC-03). Core CRUD/lookup-by-ID —
/// find by email, create, check a password — goes through <c>UserManager&lt;User&gt;</c> now
/// (see <c>Bitstream.Infrastructure.Persistence.Identity.BitstreamUserStore</c>).
/// <para>
/// Entities returned here are tracked by the caller's <see cref="IUnitOfWork"/> scope, same as
/// every other repository in this file.
/// </para>
/// </summary>
public interface IUserRepository
{
    /// <summary>Every active user of an ISP, tracked — for cascading a lock across all of them (TR-SEC-13).</summary>
    Task<IReadOnlyList<User>> GetByIspIdAsync(long ispId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Case-insensitive substring match against full name and email, most recently created
    /// first. <paramref name="ispId"/> restricts to one ISP's users — the ownership scoping
    /// <c>AdministrationService.SearchUsersAsync</c> applies before calling this, not a filter
    /// the caller opts into.
    /// </summary>
    Task<(IReadOnlyList<User> Items, int TotalCount)> SearchAsync(
        string? search, long? ispId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Most recent password hashes first, for the no-reuse rule (TR-SEC-03).</summary>
    Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken cancellationToken = default);

    Task AddPasswordHistoryAsync(long userId, string passwordHash, string algorithmTag, CancellationToken cancellationToken = default);
}

/// <summary>ISP data access, TR-SEC-15/16.</summary>
public interface IIspRepository
{
    Task<Isp?> FindByIdAsync(long ispId, CancellationToken cancellationToken = default);

    Task<bool> NiptExistsAsync(string nipt, CancellationToken cancellationToken = default);

    Task AddAsync(Isp isp, CancellationToken cancellationToken = default);

    /// <summary>Resolves the ISP a BI or CRM record belongs to (TR-PAS-04); null when the BP is not a known ISP.</summary>
    Task<Isp?> FindByCrmBpReferenceAsync(string crmBpReference, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive substring match against name and NIPT, most recently created first.</summary>
    Task<(IReadOnlyList<Isp> Items, int TotalCount)> SearchAsync(
        string? search, int skip, int take, CancellationToken cancellationToken = default);
}

/// <summary>Session store, TR-SEC-07.</summary>
public interface IUserSessionStore
{
    Task AddAsync(UserSession session, CancellationToken cancellationToken = default);

    /// <summary>By the SHA-256 hash of the raw token; includes the user, role, permissions and ISP for the authentication handler.</summary>
    Task<UserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every still-active session of a user in one statement, without loading them
    /// individually — used when a user or their ISP is locked (TR-SEC-07, TR-SEC-13).
    /// </summary>
    Task<int> RevokeAllForUserAsync(long userId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);

    /// <summary>Same, across every user of an ISP — used when the ISP itself is locked (TR-SEC-13).</summary>
    Task<int> RevokeAllForIspAsync(long ispId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default);
}

/// <summary>Second-factor challenge store, TR-SEC-04.</summary>
public interface ITwoFactorChallengeStore
{
    Task AddAsync(TwoFactorChallenge challenge, CancellationToken cancellationToken = default);

    /// <summary>Includes the user, for verification against their TOTP secret or code hash.</summary>
    Task<TwoFactorChallenge?> FindByTokenAsync(string challengeToken, CancellationToken cancellationToken = default);
}
