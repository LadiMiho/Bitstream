using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Turns a status change of an activation request or a post-activation request into in-app
/// notifications (portal.UserNotification), as part of the same save that changes the status.
/// <para>
/// It runs from <see cref="BitstreamDbContext.SaveChangesAsync(bool, CancellationToken)"/>, so every
/// status change is covered whichever service, event handler or dispatcher made it, and the
/// notifications are written in the same transaction as the change. Which changes notify whom
/// is decided by the enabled <see cref="NotificationRule"/>s: ISP users are the active users of the
/// request's ISP, service desk users the active users with the ServiceDesk role. The user who made
/// the change is never notified of it.
/// </para>
/// <para>
/// A failure here is logged and swallowed: a notification must never stop the change itself
/// from being saved.
/// </para>
/// </summary>
public sealed class StatusChangeNotifications
{
    private const int MaxDetailLength = 1000;

    private readonly ICurrentUserContext _currentUser;
    private readonly IClock _clock;
    private readonly ILogger<StatusChangeNotifications> _logger;

    public StatusChangeNotifications(ICurrentUserContext currentUser, IClock clock, ILogger<StatusChangeNotifications> logger)
    {
        _currentUser = currentUser;
        _clock = clock;
        _logger = logger;
    }

    private sealed record StatusChange(string EntityType, string PublicId, long IspId, string? PreviousStatus, string Status, string? Detail)
    {
        public string EventCode => NotificationAudiences.EventCode(EntityType, Status);
    }

    internal async Task AddAsync(BitstreamDbContext dbContext, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.DetectChanges();

        var changes = FindStatusChanges(dbContext);

        if (changes.Count == 0)
        {
            return;
        }

        try
        {
            var eventCodes = changes.Select(change => change.EventCode).Distinct().ToList();
            var rules = await dbContext.NotificationRules
                .AsNoTracking()
                .Where(rule => rule.IsEnabled && eventCodes.Contains(rule.EventCode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (rules.Count == 0)
            {
                return;
            }

            var actorId = _currentUser.UserId;
            var now = _clock.UtcNow;
            var notifications = new List<UserNotification>();

            foreach (var change in changes)
            {
                var audiences = rules.Where(rule => rule.EventCode == change.EventCode).Select(rule => rule.Audience).ToHashSet(StringComparer.Ordinal);
                var recipients = new HashSet<long>();

                if (audiences.Contains(NotificationAudiences.IspUsers))
                {
                    recipients.UnionWith(await dbContext.Users.AsNoTracking()
                        .Where(user => user.IspId == change.IspId && user.Status == UserStatus.Active)
                        .Select(user => user.Id)
                        .ToListAsync(cancellationToken).ConfigureAwait(false));
                }

                if (audiences.Contains(NotificationAudiences.ServiceDesk))
                {
                    recipients.UnionWith(await dbContext.Users.AsNoTracking()
                        .Where(user => user.Role.Name == NotificationAudiences.ServiceDeskRoleName && user.Status == UserStatus.Active)
                        .Select(user => user.Id)
                        .ToListAsync(cancellationToken).ConfigureAwait(false));
                }

                if (actorId is { } actor)
                {
                    recipients.Remove(actor);
                }

                notifications.AddRange(recipients.Select(userId => new UserNotification
                {
                    UserId = userId,
                    EntityType = change.EntityType,
                    EntityPublicId = change.PublicId,
                    Status = change.Status,
                    PreviousStatus = change.PreviousStatus,
                    Detail = change.Detail is { Length: > MaxDetailLength } detail ? detail[..MaxDetailLength] : change.Detail,
                    CreatedAt = now
                }));
            }

            dbContext.UserNotifications.AddRange(notifications);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not create the notifications for {Count} status change(s); the changes are saved without them.", changes.Count);
        }
    }

    private static List<StatusChange> FindStatusChanges(BitstreamDbContext dbContext)
    {
        var changes = new List<StatusChange>();

        foreach (var entry in dbContext.ChangeTracker.Entries<ActivationRequest>())
        {
            var request = entry.Entity;
            var previous = Previous(entry.State, entry.Property(r => r.Status).OriginalValue.ToString(), request.Status.ToString(), entry.Property(r => r.Status).IsModified);

            if (previous is { Changed: true })
            {
                changes.Add(new StatusChange(
                    NotificationEntityTypes.ActivationRequest, request.PublicId, request.IspId, previous.Value.From, request.Status.ToString(),
                    request.Status switch
                    {
                        ActivationRequestStatus.WaitingForServiceDesk => request.OperatorComment,
                        ActivationRequestStatus.ActivationFailed or ActivationRequestStatus.Completed when request.ServiceDeskComment is not null => request.ServiceDeskComment,
                        _ => request.StatusReason
                    }));
            }
        }

        foreach (var entry in dbContext.ChangeTracker.Entries<PostActivationRequest>())
        {
            var request = entry.Entity;
            var previous = Previous(entry.State, entry.Property(r => r.Status).OriginalValue, request.Status, entry.Property(r => r.Status).IsModified);

            if (previous is { Changed: true })
            {
                changes.Add(new StatusChange(
                    NotificationEntityTypes.PostActivationRequest, request.PublicId, request.IspId, previous.Value.From, request.Status,
                    request.Status switch
                    {
                        PostActivationRequestStatus.TechnicallyCompleted => request.CompletionReason,
                        PostActivationRequestStatus.Failed => request.CrmMessage,
                        _ => null
                    }));
            }
        }

        return changes;
    }

    /// <summary>A new row counts as a change from nothing; a modified one only when its status really differs.</summary>
    private static (bool Changed, string? From)? Previous(EntityState state, string? original, string current, bool modified) => state switch
    {
        EntityState.Added => (true, null),
        EntityState.Modified when modified && !string.Equals(original, current, StringComparison.Ordinal) => (true, original),
        _ => null
    };
}
