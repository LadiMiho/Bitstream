using System.Text.Json;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services.Notifications;

/// <summary>
/// The status changes that can notify someone, in the order they are shown on the Notification
/// settings screen (the order requests move through them).
/// </summary>
public static class NotificationEventCatalogue
{
    public static readonly IReadOnlyList<string> ActivationStatuses =
    [
        nameof(ActivationRequestStatus.Submitted),
        nameof(ActivationRequestStatus.PendingCrmSync),
        nameof(ActivationRequestStatus.AwaitingGisVerification),
        nameof(ActivationRequestStatus.LineAvailable),
        nameof(ActivationRequestStatus.RejectedNoLine),
        nameof(ActivationRequestStatus.SalesOrderOpened),
        nameof(ActivationRequestStatus.AwaitingOperatorConfirmation),
        nameof(ActivationRequestStatus.WaitingForServiceDesk),
        nameof(ActivationRequestStatus.Completed),
        nameof(ActivationRequestStatus.ActivationFailed),
        nameof(ActivationRequestStatus.Closed),
        nameof(ActivationRequestStatus.IntegrationFailed)
    ];

    public static readonly IReadOnlyList<string> PostActivationStatuses =
    [
        PostActivationRequestStatus.PendingCrmSync,
        PostActivationRequestStatus.InProcess,
        PostActivationRequestStatus.TechnicallyCompleted,
        PostActivationRequestStatus.Failed
    ];

    public static readonly IReadOnlyList<string> Audiences = [NotificationAudiences.IspUsers, NotificationAudiences.ServiceDesk];

    /// <summary>Every (entity type, status) pair the settings screen offers.</summary>
    public static IEnumerable<(string EntityType, string Status)> All() =>
        ActivationStatuses.Select(status => (NotificationEntityTypes.ActivationRequest, status))
            .Concat(PostActivationStatuses.Select(status => (NotificationEntityTypes.PostActivationRequest, status)));
}

/// <summary>What the header bell shows: how many are unread and the newest of them.</summary>
public sealed record NotificationSummary(int UnreadCount, IReadOnlyList<UserNotification> Latest);

/// <summary>One row of the Notification settings screen: a status change and whom it notifies.</summary>
public sealed record NotificationSetting(string EntityType, string Status, bool IspUsers, bool ServiceDesk);

public interface IUserNotificationService
{
    /// <summary>The bell: unread count and up to <paramref name="take"/> newest unread notifications.</summary>
    Task<NotificationSummary> GetSummaryAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>The Notifications page: the current user's notifications, newest first.</summary>
    Task<PagedResult<UserNotification>> SearchAsync(bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Marks one of the current user's notifications read and returns it; null when it is not theirs.</summary>
    Task<UserNotification?> OpenAsync(long notificationId, CancellationToken cancellationToken = default);

    /// <summary>Marks every unread notification of the current user read; returns how many.</summary>
    Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Every status change in <see cref="NotificationEventCatalogue"/> with its current switches.</summary>
    Task<IReadOnlyList<NotificationSetting>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the switches: <paramref name="enabled"/> holds the (event code, audience) pairs that are on; every other one is off. Audited.</summary>
    Task SaveSettingsAsync(IReadOnlyCollection<(string EventCode, string Audience)> enabled, CancellationToken cancellationToken = default);
}

/// <summary>Implements <see cref="IUserNotificationService"/>. Notifications are created by the persistence layer when a request's status changes.</summary>
public sealed class UserNotificationService : IUserNotificationService
{
    private readonly IUserNotificationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _auditWriter;
    private readonly ICurrentUserContext _currentUser;
    private readonly IClock _clock;

    public UserNotificationService(
        IUserNotificationRepository repository,
        IUnitOfWork unitOfWork,
        IAuditWriter auditWriter,
        ICurrentUserContext currentUser,
        IClock clock)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _auditWriter = auditWriter;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<NotificationSummary> GetSummaryAsync(int take, CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return new NotificationSummary(0, []);
        }

        var (items, total) = await _repository.SearchAsync(userId, unreadOnly: true, 0, Math.Clamp(take, 1, 50), cancellationToken).ConfigureAwait(false);

        return new NotificationSummary(total, items);
    }

    public async Task<PagedResult<UserNotification>> SearchAsync(bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return new PagedResult<UserNotification>([], 0);
        }

        var (items, total) = await _repository.SearchAsync(userId, unreadOnly, Math.Max(0, skip), Math.Clamp(take, 1, 100), cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<UserNotification>(items, total);
    }

    public async Task<UserNotification?> OpenAsync(long notificationId, CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return null;
        }

        var notification = await _repository.FindAsync(userId, notificationId, cancellationToken).ConfigureAwait(false);

        if (notification is { ReadAt: null })
        {
            notification.ReadAt = _clock.UtcNow;
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return notification;
    }

    public async Task<int> MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return 0;
        }

        var unread = await _repository.GetUnreadAsync(userId, cancellationToken).ConfigureAwait(false);
        var now = _clock.UtcNow;

        foreach (var notification in unread)
        {
            notification.ReadAt = now;
        }

        if (unread.Count > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return unread.Count;
    }

    public async Task<IReadOnlyList<NotificationSetting>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var rules = await _repository.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        var enabled = rules.Where(rule => rule.IsEnabled).Select(rule => (rule.EventCode, rule.Audience)).ToHashSet();

        return
        [
            .. NotificationEventCatalogue.All().Select(item =>
            {
                var code = NotificationAudiences.EventCode(item.EntityType, item.Status);
                return new NotificationSetting(
                    item.EntityType, item.Status,
                    enabled.Contains((code, NotificationAudiences.IspUsers)),
                    enabled.Contains((code, NotificationAudiences.ServiceDesk)));
            })
        ];
    }

    public async Task SaveSettingsAsync(IReadOnlyCollection<(string EventCode, string Audience)> enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enabled);

        var known = NotificationEventCatalogue.All().Select(item => NotificationAudiences.EventCode(item.EntityType, item.Status)).ToHashSet(StringComparer.Ordinal);
        var wanted = enabled
            .Where(pair => known.Contains(pair.EventCode) && NotificationEventCatalogue.Audiences.Contains(pair.Audience))
            .ToHashSet();

        var rules = await _repository.GetRulesAsync(cancellationToken).ConfigureAwait(false);
        var byKey = rules.ToDictionary(rule => (rule.EventCode, rule.Audience));
        var before = rules.Where(rule => rule.IsEnabled).Select(rule => $"{rule.EventCode}/{rule.Audience}").Order(StringComparer.Ordinal).ToList();
        var now = _clock.UtcNow;

        foreach (var code in known)
        {
            foreach (var audience in NotificationEventCatalogue.Audiences)
            {
                var on = wanted.Contains((code, audience));

                if (byKey.TryGetValue((code, audience), out var rule))
                {
                    if (rule.IsEnabled != on)
                    {
                        rule.IsEnabled = on;
                        rule.UpdatedAt = now;
                        rule.UpdatedBy = _currentUser.UserId;
                    }
                }
                else if (on)
                {
                    _repository.AddRule(new NotificationRule
                    {
                        EventCode = code,
                        Audience = audience,
                        IsEnabled = true,
                        UpdatedAt = now,
                        UpdatedBy = _currentUser.UserId
                    });
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var after = wanted.Select(pair => $"{pair.EventCode}/{pair.Audience}").Order(StringComparer.Ordinal).ToList();
        await _auditWriter.WriteAsync(
            "NotificationSettings.Updated", "NotificationRule", null,
            JsonSerializer.Serialize(new { enabled = before }),
            JsonSerializer.Serialize(new { enabled = after }),
            cancellationToken).ConfigureAwait(false);
    }
}
