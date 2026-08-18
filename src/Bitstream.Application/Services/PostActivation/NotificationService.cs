using System.Text;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;

namespace Bitstream.Application.Services.PostActivation;

/// <summary>
/// Implements <see cref="INotificationService"/>: TRD 8. Queues only — persists a
/// <see cref="Notification"/> row with <see cref="Domain.Enums.NotificationStatus.Pending"/> and
/// stops. Recipient distribution-group expansion (TR-NTF-02) and template rendering from an
/// external source (TR-NTF-01) are TRD 11.4 open items 6 and 7; until they land, the subject and
/// body are built from the template code and variables directly, and the recipients list is
/// literally the template code's key in the distribution-group configuration — enough to prove
/// the ticket lifecycle produces the right notification at the right point (TR-PAS-13 to
/// TR-PAS-17) without depending on either open item. Actually sending is
/// <c>IEmailGateway</c>'s job, dispatched from the outbox the same way CRM messages are.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly INotificationRepository _notificationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public NotificationService(INotificationRepository notificationRepository, IUnitOfWork unitOfWork, IClock clock)
    {
        _notificationRepository = notificationRepository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Notification> QueueAsync(
        string templateCode,
        IReadOnlyDictionary<string, string> variables,
        string relatedEntityType,
        long? relatedEntityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateCode);
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedEntityType);

        var body = new StringBuilder();

        foreach (var (key, value) in variables)
        {
            body.Append(key).Append(": ").AppendLine(value);
        }

        var notification = new Notification
        {
            TemplateCode = templateCode,
            Recipients = templateCode,
            Subject = templateCode,
            BodyRendered = body.ToString(),
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = relatedEntityId,
            RelatedEntityPublicId = variables.GetValueOrDefault("ticketPublicId") ?? variables.GetValueOrDefault("requestPublicId"),
            CreatedAt = _clock.UtcNow
        };

        await _notificationRepository.AddAsync(notification, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return notification;
    }
}
