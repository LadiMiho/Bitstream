using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IUserRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly BitstreamDbContext _dbContext;

    public UserRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        WithGraph(_dbContext.Users)
            // TR-SEC-01: unique across the platform; case-insensitive matches how the column is
            // collated (SQL Server's default collation is case-insensitive), so lookup and
            // uniqueness agree.
            .FirstOrDefaultAsync(user => user.Email.ToLower() == email.ToLower(), cancellationToken);

    public Task<User?> FindByIdAsync(long userId, CancellationToken cancellationToken = default) =>
        WithGraph(_dbContext.Users).FirstOrDefaultAsync(user => user.UserId == userId, cancellationToken);

    public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken = default) =>
        _dbContext.Users.AnyAsync(user => user.Email.ToLower() == email.ToLower(), cancellationToken);

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await _dbContext.Users.AddAsync(user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<User>> GetByIspIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        await WithGraph(_dbContext.Users)
            .Where(user => user.IspId == ispId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetRecentPasswordHashesAsync(long userId, int count, CancellationToken cancellationToken = default) =>
        await _dbContext.UserPasswordHistory
            .Where(history => history.UserId == userId)
            .OrderByDescending(history => history.CreatedAt)
            .Take(count)
            .Select(history => history.PasswordHash)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddPasswordHistoryAsync(long userId, string passwordHash, string algorithmTag, CancellationToken cancellationToken = default)
    {
        _dbContext.UserPasswordHistory.Add(new UserPasswordHistory
        {
            UserId = userId,
            PasswordHash = passwordHash,
            PasswordHashAlgorithm = algorithmTag,
            CreatedAt = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }

    /// <summary>Role, its permissions, and the ISP — everything <c>AuthenticatedUser</c> and the RBAC checks need in one round trip.</summary>
    private static IQueryable<User> WithGraph(IQueryable<User> query) =>
        query
            .Include(user => user.Role)
                .ThenInclude(role => role.RolePermissions)
                    .ThenInclude(rolePermission => rolePermission.Permission)
            .Include(user => user.Isp);
}
