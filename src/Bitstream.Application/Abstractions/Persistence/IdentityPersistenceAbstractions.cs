using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>
/// Everything about a user that <c>UserManager&lt;User&gt;</c> does not cover, because it isn't
/// part of Identity's contract: browsing/searching (TRD 4.2), cascading a lock across an ISP's
/// users (TR-SEC-13), and the password-reuse history (TR-SEC-03). Core CRUD/lookup-by-ID —
/// find by email, create, check a password — goes through <c>UserManager&lt;User&gt;</c> now,
/// backed by ASP.NET Core Identity's own EF store
/// (<c>Bitstream.Infrastructure.Persistence.Identity.BitstreamIdentityDbContext</c>).
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
