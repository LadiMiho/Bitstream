using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IUserNotificationRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class UserNotificationRepository : IUserNotificationRepository
{
    private readonly BitstreamDbContext _dbContext;

    public UserNotificationRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken = default) =>
        _dbContext.UserNotifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

    public async Task<(IReadOnlyList<UserNotification> Items, int TotalCount)> SearchAsync(
        long userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.UserNotifications.AsNoTracking().Where(n => n.UserId == userId);

        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.NotificationId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    public Task<UserNotification?> FindAsync(long userId, long notificationId, CancellationToken cancellationToken = default) =>
        _dbContext.UserNotifications.FirstOrDefaultAsync(n => n.NotificationId == notificationId && n.UserId == userId, cancellationToken);

    public async Task<IReadOnlyList<UserNotification>> GetUnreadAsync(long userId, CancellationToken cancellationToken = default) =>
        await _dbContext.UserNotifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<NotificationRule>> GetRulesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.NotificationRules.ToListAsync(cancellationToken).ConfigureAwait(false);

    public void AddRule(NotificationRule rule) => _dbContext.NotificationRules.Add(rule);
}
