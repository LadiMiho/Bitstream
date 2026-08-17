using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IUserSessionStore"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class UserSessionStore : IUserSessionStore
{
    private readonly BitstreamDbContext _dbContext;

    public UserSessionStore(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task AddAsync(UserSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _dbContext.UserSessions.AddAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserSession?> FindByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        _dbContext.UserSessions
            .Include(session => session.User)
                .ThenInclude(user => user.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .FirstOrDefaultAsync(session => session.TokenHash == tokenHash, cancellationToken);

    public Task<int> RevokeAllForUserAsync(long userId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
        _dbContext.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAt, revokedAt)
                    .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);

    public Task<int> RevokeAllForIspAsync(long ispId, string reason, DateTimeOffset revokedAt, CancellationToken cancellationToken = default) =>
        _dbContext.UserSessions
            .Where(session => session.User.IspId == ispId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAt, revokedAt)
                    .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);
}
