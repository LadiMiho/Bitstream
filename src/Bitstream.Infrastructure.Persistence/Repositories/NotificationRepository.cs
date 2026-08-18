using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;

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
}
