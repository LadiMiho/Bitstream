using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Application.Services.Notifications;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Turns a status change of an activation request or a post-activation request into in-app
/// notifications (portal.UserNotification) and queued notification emails (ops.Notification, sent
/// by <see cref="EmailNotificationDispatcher"/>), as part of the same save that changes the status.
/// <para>
/// It runs from <see cref="BitstreamDbContext.SaveChangesAsync(bool, CancellationToken)"/>, so every
/// status change is covered whichever service, event handler or dispatcher made it, and the
/// notifications are written in the same transaction as the change. Which changes notify whom
/// is decided by the <see cref="NotificationRule"/>s, separately for the portal and for email: ISP
/// users are the active users of the request's ISP, service desk users the active users with the
/// ServiceDesk role. The user who made the change is never notified of it. Each email is rendered
/// now, from <see cref="EmailNotificationOptions"/>, so the log keeps exactly what was sent (TR-NTF-05).
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
    private readonly IOptionsMonitor<EmailNotificationOptions> _emailOptions;
    private readonly ILogger<StatusChangeNotifications> _logger;

    public StatusChangeNotifications(
        ICurrentUserContext currentUser,
        IClock clock,
        IOptionsMonitor<EmailNotificationOptions> emailOptions,
        ILogger<StatusChangeNotifications> logger)
    {
        _currentUser = currentUser;
        _clock = clock;
        _emailOptions = emailOptions;
        _logger = logger;
    }

    private sealed record StatusChange(
        string EntityType, long? EntityId, string PublicId, long IspId, string? PreviousStatus, string Status, string? Detail)
    {
        public string EventCode => NotificationAudiences.EventCode(EntityType, Status);
    }

    private sealed record Recipient(long UserId, string? Email, string FullName);

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
                .Where(rule => (rule.IsEnabled || rule.EmailEnabled) && eventCodes.Contains(rule.EventCode))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (rules.Count == 0)
            {
                return;
            }

            var emailOptions = _emailOptions.CurrentValue;
            var now = _clock.UtcNow;
            var ispNames = new Dictionary<long, string>();

            foreach (var change in changes)
            {
                var changeRules = rules.Where(rule => rule.EventCode == change.EventCode).ToList();
                var audiences = new Dictionary<string, IReadOnlyList<Recipient>>(StringComparer.Ordinal);

                foreach (var audience in changeRules.Select(rule => rule.Audience).Distinct())
                {
                    audiences[audience] = await RecipientsAsync(dbContext, audience, change.IspId, cancellationToken).ConfigureAwait(false);
                }

                var notified = new HashSet<long>();
                foreach (var rule in changeRules.Where(rule => rule.IsEnabled))
                {
                    foreach (var recipient in audiences[rule.Audience].Where(r => notified.Add(r.UserId)))
                    {
                        dbContext.UserNotifications.Add(new UserNotification
                        {
                            UserId = recipient.UserId,
                            EntityType = change.EntityType,
                            EntityPublicId = change.PublicId,
                            Status = change.Status,
                            PreviousStatus = change.PreviousStatus,
                            Detail = Truncate(change.Detail),
                            CreatedAt = now
                        });
                    }
                }

                if (!emailOptions.Enabled)
                {
                    continue;
                }

                var emailed = new HashSet<long>();
                foreach (var rule in changeRules.Where(rule => rule.EmailEnabled))
                {
                    foreach (var recipient in audiences[rule.Audience].Where(r => !string.IsNullOrWhiteSpace(r.Email) && emailed.Add(r.UserId)))
                    {
                        if (!ispNames.TryGetValue(change.IspId, out var ispName))
                        {
                            ispName = await dbContext.Isps.AsNoTracking().Where(isp => isp.IspId == change.IspId).Select(isp => isp.Name)
                                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
                            ispNames[change.IspId] = ispName;
                        }

                        var email = RenderEmail(emailOptions, change, rule.Audience, recipient, ispName);
                        dbContext.Notifications.Add(new Notification
                        {
                            TemplateCode = email.TemplateKey,
                            Recipients = recipient.Email!,
                            Subject = email.Subject.Length > 500 ? email.Subject[..500] : email.Subject,
                            BodyRendered = email.Body,
                            RelatedEntityType = change.EntityType,
                            RelatedEntityId = change.EntityId,
                            RelatedEntityPublicId = change.PublicId,
                            CreatedAt = now,
                            CorrelationId = _currentUser.CorrelationId
                        });
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Could not create the notifications for {Count} status change(s); the changes are saved without them.", changes.Count);
        }
    }

    /// <summary>ISP users: the active users of the request's ISP. Service desk: the active users with the ServiceDesk role. Never the user who made the change.</summary>
    private async Task<IReadOnlyList<Recipient>> RecipientsAsync(BitstreamDbContext dbContext, string audience, long ispId, CancellationToken cancellationToken)
    {
        var users = dbContext.Users.AsNoTracking().Where(user => user.Status == UserStatus.Active);
        users = audience switch
        {
            NotificationAudiences.IspUsers => users.Where(user => user.IspId == ispId),
            NotificationAudiences.ServiceDesk => users.Where(user => user.Role.Name == NotificationAudiences.ServiceDeskRoleName),
            _ => users.Where(_ => false)
        };

        var actorId = _currentUser.UserId;
        return await users
            .Where(user => actorId == null || user.Id != actorId)
            .OrderBy(user => user.Id)
            .Select(user => new Recipient(user.Id, user.Email, user.FullName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static RenderedEmail RenderEmail(EmailNotificationOptions options, StatusChange change, string audience, Recipient recipient, string ispName)
    {
        var (key, template) = EmailNotificationRenderer.Resolve(options, change.EntityType, change.Status, audience);
        var status = RequestStatusText.For(change.EntityType, change.Status);
        var previous = change.PreviousStatus is null ? null : RequestStatusText.For(change.EntityType, change.PreviousStatus);
        var link = string.IsNullOrWhiteSpace(options.PortalBaseUrl)
            ? null
            : options.PortalBaseUrl.TrimEnd('/') + RequestStatusText.RequestPath(change.EntityType, change.PublicId);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["RecipientName"] = recipient.FullName,
            ["RequestType"] = RequestStatusText.RequestType(change.EntityType),
            ["RequestId"] = change.PublicId,
            ["IspName"] = ispName,
            ["Status"] = status,
            ["PreviousStatus"] = previous,
            ["Change"] = previous is null ? $"has been created with the status {status}" : $"has moved from {previous} to {status}",
            ["Reason"] = change.Detail,
            ["Link"] = link
        };

        return EmailNotificationRenderer.Render(key, template, values, options.IsHtml);
    }

    private static string? Truncate(string? detail) => detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;

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
                    NotificationEntityTypes.ActivationRequest, entry.State == EntityState.Added ? null : request.RequestId, request.PublicId, request.IspId, previous.Value.From, request.Status.ToString(),
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
                    NotificationEntityTypes.PostActivationRequest, entry.State == EntityState.Added ? null : request.RequestId, request.PublicId, request.IspId, previous.Value.From, request.Status,
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
