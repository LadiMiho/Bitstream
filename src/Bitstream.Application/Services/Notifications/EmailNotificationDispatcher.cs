using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Abstractions.Time;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Notifications;

/// <summary>
/// Sends the queued notification emails (ops.Notification, written when a request's status
/// changes) through <see cref="IEmailGateway"/> (TR-NTF-03, TR-NTF-04). Runs only in the host that
/// runs the background jobs (Bitstream.Api), like the CRM outbox.
/// <para>
/// A mail the gateway accepted is Sent. A technical failure (gateway down, timeout, 5xx) is tried
/// again on later polls, up to <see cref="EmailNotificationOptions.MaxAttempts"/> times; a refusal
/// (4xx, gateway not configured) or the last failed attempt makes it Failed, with the reason kept
/// in LastError. Sending never affects the request whose change the mail reports.
/// </para>
/// </summary>
public sealed class EmailNotificationDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<EmailNotificationOptions> _options;
    private readonly ILogger<EmailNotificationDispatcher> _logger;

    public EmailNotificationDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<EmailNotificationOptions> options,
        ILogger<EmailNotificationDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            _options.CurrentValue.Enabled
                ? "Notification email dispatcher started: polling every {PollInterval}."
                : "Notification emails are disabled (Notifications:Email:Enabled=false); polling every {PollInterval} in case they are turned on.",
            _options.CurrentValue.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.CurrentValue.Enabled)
            {
                try
                {
                    await DispatchBatchAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Notification email cycle failed; will retry on the next poll.");
                }
            }

            try
            {
                await Task.Delay(_options.CurrentValue.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }
    }

    /// <summary>Sends one batch; returns how many were sent. Exposed so tests can drive it without the timer.</summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        var gateway = scope.ServiceProvider.GetRequiredService<IEmailGateway>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var due = await repository.GetDueEmailsAsync(Math.Max(1, options.MaxAttempts), Math.Max(1, options.BatchSize), cancellationToken).ConfigureAwait(false);
        var sent = 0;

        foreach (var notification in due)
        {
            var result = await SendAsync(gateway, notification, cancellationToken).ConfigureAwait(false);
            notification.Attempts++;

            if (result.IsSuccess)
            {
                notification.Status = NotificationStatus.Sent;
                notification.SentAt = clock.UtcNow;
                notification.LastError = null;
                sent++;
            }
            else
            {
                notification.LastError = Truncate(result.ErrorMessage ?? result.Outcome.ToString());

                if (!result.IsRetryable || notification.Attempts >= options.MaxAttempts)
                {
                    notification.Status = NotificationStatus.Failed;
                    _logger.LogWarning(
                        "Notification email {NotificationId} about {RelatedPublicId} failed for good after {Attempts} attempt(s): {Error}",
                        notification.NotificationId, notification.RelatedEntityPublicId, notification.Attempts, notification.LastError);
                }
                else
                {
                    _logger.LogInformation(
                        "Notification email {NotificationId} about {RelatedPublicId} failed (attempt {Attempts} of {MaxAttempts}); will retry: {Error}",
                        notification.NotificationId, notification.RelatedEntityPublicId, notification.Attempts, options.MaxAttempts, notification.LastError);
                }
            }

            // Saved one by one, so a crash part-way never sends a mail twice.
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    private static Task<IntegrationResult<bool>> SendAsync(IEmailGateway gateway, Notification notification, CancellationToken cancellationToken)
    {
        var html = notification.BodyRendered.StartsWith("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase);
        var envelope = new IntegrationEnvelope(
            Guid.NewGuid(),
            notification.CorrelationId ?? Guid.NewGuid().ToString("N"),
            $"notification-{notification.NotificationId}",
            notification.CreatedAt);

        return gateway.SendAsync(
            new EmailMessage(
                envelope,
                [notification.Recipients],
                [],
                notification.Subject,
                html ? notification.BodyRendered : string.Empty,
                html ? string.Empty : notification.BodyRendered),
            cancellationToken);
    }

    private static string Truncate(string error) => error.Length > 2000 ? error[..2000] : error;
}
