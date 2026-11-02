using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="INotificationRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class NotificationRepository : INotificationRepository
{
    private readonly BitstreamDbContext _dbContext;

    public NotificationRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task AddAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        await _dbContext.Notifications.AddAsync(notification, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Notification>> GetDueEmailsAsync(int maxAttempts, int take, CancellationToken cancellationToken = default) =>
        await _dbContext.Notifications
            .Where(n => n.Status == NotificationStatus.Pending && n.Attempts < maxAttempts && n.Recipients.Contains("@"))
            .OrderBy(n => n.CreatedAt)
            .ThenBy(n => n.NotificationId)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
