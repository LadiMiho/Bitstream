using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Identity.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IUserRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly BitstreamDbContext _dbContext;

    public UserRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<User>> GetByIspIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        await _dbContext.Users
            .Where(user => user.IspId == ispId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<(IReadOnlyList<User> Items, int TotalCount)> SearchAsync(
        string? search, long? ispId, string? roleName, string? status, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Users.Include(user => user.Role).AsQueryable();

        if (ispId is { } id)
        {
            query = query.Where(user => user.IspId == id);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = search.ToUpper();
            query = query.Where(user => user.FullName.ToUpper().Contains(pattern) || user.Email!.ToUpper().Contains(pattern));
        }

        if (!string.IsNullOrWhiteSpace(roleName))
        {
            query = query.Where(user => user.Role!.Name == roleName);
        }

        if (string.Equals(status, "Locked", StringComparison.Ordinal))
        {
            query = query.Where(user => user.LockoutEnd != null && user.LockoutEnd > DateTimeOffset.UtcNow);
        }
        else if (string.Equals(status, "Active", StringComparison.Ordinal))
        {
            query = query.Where(user => user.LockoutEnd == null || user.LockoutEnd <= DateTimeOffset.UtcNow);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(user => user.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

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
}
