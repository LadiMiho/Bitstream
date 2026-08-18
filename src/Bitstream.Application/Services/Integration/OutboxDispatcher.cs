using System.Text.Json;
using Bitstream.Application.Abstractions;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services.Activation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Application.Services.Integration;

/// <summary>
/// Claims due outbound <see cref="IntegrationMessage"/> rows and dispatches each to the gateway
/// for its target system (TR-ARC-03). This is the "background dispatcher, not the request
/// thread" that <see cref="IUnitOfWork"/> and <see cref="IIntegrationOutbox"/> were built for.
/// <para>
/// A singleton hosted service resolving scoped dependencies per cycle, the same pattern ASP.NET
/// Core's own background services use — <see cref="IIntegrationOutbox"/>, the repositories and
/// <c>BitstreamDbContext</c> underneath them are all scoped, so each dispatch cycle gets its own.
/// </para>
/// <para>
/// Only CRM (INT-CRM-01, INT-CRM-02) is wired. A message for a target system with no case below
/// is left unclaimed-again by marking it a non-retryable failure — it dead-letters rather than
/// spinning forever, so a future target system that forgets to register here fails loudly.
/// </para>
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<OutboxDispatcherOptions> _options;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OutboxDispatcherOptions> options,
        ILogger<OutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    _logger.LogError(exception, "Outbox dispatch cycle failed; will retry on the next poll.");
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

    /// <summary>
    /// Claims and dispatches one batch. Exposed separately from <see cref="ExecuteAsync"/> so
    /// tests can drive dispatch deterministically instead of racing the poll timer.
    /// </summary>
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IIntegrationOutbox>();

        var claimed = await outbox.ClaimDueOutboundAsync(_options.CurrentValue.BatchSize, cancellationToken).ConfigureAwait(false);

        foreach (var message in claimed)
        {
            await DispatchOneAsync(scope.ServiceProvider, message, cancellationToken).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task DispatchOneAsync(IServiceProvider services, IntegrationMessage message, CancellationToken cancellationToken)
    {
        var correlationContext = services.GetRequiredService<ICorrelationContext>();

        using (correlationContext.BeginScope(message.CorrelationId))
        {
            try
            {
                switch (message.TargetSystem, message.InterfaceCode)
                {
                    case (TargetSystem.Crm, "INT-CRM-01"):
                        await DispatchCreateCustomerAsync(services, message, cancellationToken).ConfigureAwait(false);
                        break;

                    case (TargetSystem.Crm, "INT-CRM-02"):
                        await DispatchCreateTicketAsync(services, message, cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        _logger.LogError(
                            "No dispatcher registered for {TargetSystem}/{InterfaceCode}; dead-lettering message {MessageId}.",
                            message.TargetSystem, message.InterfaceCode, message.MessageId);
                        await services.GetRequiredService<IIntegrationOutbox>().MarkFailedAsync(
                            message.MessageId, "No dispatcher registered for this interface.", retryable: false, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Dispatch of message {MessageId} threw unexpectedly.", message.MessageId);
                await HandleFailureAsync(services, message, exception.Message, isRetryable: true, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchCreateCustomerAsync(IServiceProvider services, IntegrationMessage message, CancellationToken cancellationToken)
    {
        var command = Deserialize<CreateCrmCustomerCommand>(message);
        var gateway = services.GetRequiredService<ICrmGateway>();

        var result = await gateway.CreateCustomerAsync(command, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await HandleFailureAsync(services, message, result.ErrorMessage ?? result.Outcome.ToString(), result.IsRetryable, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var outbox = services.GetRequiredService<IIntegrationOutbox>();
        await outbox.MarkSucceededAsync(message.MessageId, JsonSerializer.Serialize(result.Value), cancellationToken).ConfigureAwait(false);

        // INT-CRM-02 needs the Business Partner INT-CRM-01 just returned, so it is enqueued
        // here — with the real BP, never a placeholder — rather than by SubmitAsync up front.
        var requests = services.GetRequiredService<IActivationRequestRepository>();
        var request = await requests.FindByPublicIdAsync(command.RequestPublicId, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            _logger.LogWarning(
                "INT-CRM-01 succeeded for {RequestPublicId}, but the activation request no longer exists; INT-CRM-02 not enqueued.",
                command.RequestPublicId);
            return;
        }

        var ticketCommand = new CreateActivationTicketCommand(
            command.Envelope, request.PublicId, result.Value!.CrmCustomerId, result.Value.BusinessPartner,
            request.Classification, request.PackageCode, request.ContractDurationMonths,
            request.LocationRaw, request.LocationLat, request.LocationLng, request.Comments);

        await outbox.EnqueueOutboundAsync(
            TargetSystem.Crm, "INT-CRM-02", "CREATE_ACTIVATION_TICKET", request.PublicId,
            JsonSerializer.Serialize(ticketCommand), message.CorrelationId, request.PublicId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DispatchCreateTicketAsync(IServiceProvider services, IntegrationMessage message, CancellationToken cancellationToken)
    {
        var command = Deserialize<CreateActivationTicketCommand>(message);
        var gateway = services.GetRequiredService<ICrmGateway>();

        var result = await gateway.CreateActivationTicketAsync(command, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await HandleFailureAsync(services, message, result.ErrorMessage ?? result.Outcome.ToString(), result.IsRetryable, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var outbox = services.GetRequiredService<IIntegrationOutbox>();
        await outbox.MarkSucceededAsync(message.MessageId, JsonSerializer.Serialize(result.Value), cancellationToken).ConfigureAwait(false);

        // Both calls of Direction A's activation flow are in — drive the state machine
        // (PendingCrmSync -> AwaitingGisVerification, TRD 5.3). CrmCustomerId and
        // BusinessPartner travel forward on the ticket command itself (see
        // DispatchCreateCustomerAsync), so nothing needs to be looked up again here.
        var activationService = services.GetRequiredService<IActivationRequestService>();
        await activationService.MarkCrmSyncSucceededAsync(
            command.RequestPublicId,
            crmCustomerId: command.CrmCustomerId,
            businessPartner: command.BusinessPartner,
            crmTicketId: result.Value!.CrmTicketId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task HandleFailureAsync(
        IServiceProvider services,
        IntegrationMessage message,
        string error,
        bool isRetryable,
        CancellationToken cancellationToken)
    {
        var options = services.GetRequiredService<IOptionsMonitor<OutboxDispatcherOptions>>().CurrentValue;
        var attemptsAfterThis = message.Attempts + 1;
        var retryable = isRetryable && attemptsAfterThis < options.MaxAttempts;

        var outbox = services.GetRequiredService<IIntegrationOutbox>();
        await outbox.MarkFailedAsync(message.MessageId, error, retryable, cancellationToken).ConfigureAwait(false);

        if (!retryable && message.RelatedPublicId is { } relatedPublicId)
        {
            var activationService = services.GetRequiredService<IActivationRequestService>();

            try
            {
                await activationService.MarkCrmSyncFailedAsync(relatedPublicId, error, cancellationToken).ConfigureAwait(false);
            }
            catch (ActivationRequestConflictException)
            {
                // The request already moved on (e.g. a later attempt of the same message
                // succeeded first, or an administrator already acted) — nothing to record.
            }
            catch (ActivationRequestNotFoundException)
            {
                // Nothing left to mark.
            }
        }
    }

    private static T Deserialize<T>(IntegrationMessage message) =>
        JsonSerializer.Deserialize<T>(message.Payload) ??
        throw new InvalidOperationException($"Integration message {message.MessageId} payload could not be deserialised as {typeof(T).Name}.");
}
