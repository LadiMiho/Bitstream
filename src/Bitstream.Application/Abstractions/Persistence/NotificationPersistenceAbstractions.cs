using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>Data access for in-app notifications (portal.UserNotification) and their settings (portal.NotificationRule).</summary>
public interface IUserNotificationRepository
{
    Task<int> CountUnreadAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>The user's notifications, newest first; only unread ones when <paramref name="unreadOnly"/>.</summary>
    Task<(IReadOnlyList<UserNotification> Items, int TotalCount)> SearchAsync(
        long userId, bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>One of the user's notifications, tracked; null when it is not theirs or does not exist.</summary>
    Task<UserNotification?> FindAsync(long userId, long notificationId, CancellationToken cancellationToken = default);

    /// <summary>The user's unread notifications, tracked.</summary>
    Task<IReadOnlyList<UserNotification>> GetUnreadAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>Every stored rule, tracked.</summary>
    Task<IReadOnlyList<NotificationRule>> GetRulesAsync(CancellationToken cancellationToken = default);

    /// <summary>Stages a new rule; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    void AddRule(NotificationRule rule);
}
