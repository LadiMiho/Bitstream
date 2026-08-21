using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
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
