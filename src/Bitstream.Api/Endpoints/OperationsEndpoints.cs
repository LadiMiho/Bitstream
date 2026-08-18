using Bitstream.Api.Contracts;
using Bitstream.Api.Security;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace Bitstream.Api.Endpoints;

/// <summary>
/// Administrator-facing operational surface of the integration layer.
/// <para>
/// The outbound rows of TRD 7.1 — INT-CRM-01, -02, -04, -06, -08, -09, INT-BI-01, INT-BI-02,
/// INT-SAP-01 and INT-MAIL-01 — are not HTTP endpoints the portal exposes; they are calls the
/// portal makes, declared as ports in Bitstream.Application.Abstractions.Integration and
/// dispatched from the outbox. What the portal must expose for them is the operational
/// control the TRD requires: dead-letter inspection and replay (TR-INT-05), a manual sync
/// trigger (TR-PAS-03), sync freshness (TR-PAS-07) and the reconciliation report (TR-INT-10).
/// </para>
/// </summary>
public static class OperationsEndpoints
{
    /// <summary>Maps the operational endpoints under /api/v1/ops.</summary>
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/ops")
            .WithTags("Operations")
            .RequireRateLimiting(RateLimitPolicies.Administration);

        group.MapGet("/integration/dead-letter", GetDeadLetterQueue)
            .WithName("GetDeadLetterQueue")
            .WithSummary("List dead-lettered integration messages")
            .WithDescription(
                "TR-INT-05: messages in the dead-letter queue must be inspectable by an administrator. " +
                "Payloads are returned with sensitive fields masked (TR-INT-09).")
            .Produces<DeadLetterMessage[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequirePermission(PostActivationPermissionCodes.IntegrationDeadLetterRead);

        group.MapPost("/integration/dead-letter/{messageId:long}/replay", ReplayDeadLetterMessage)
            .WithName("ReplayDeadLetterMessage")
            .WithSummary("Re-queue a dead-lettered integration message")
            .WithDescription(
                "TR-INT-05: replay must not lose or duplicate data. The message keeps its original " +
                "idempotency key, so the receiver deduplicates a message that in fact arrived (TR-INT-03).")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequirePermission(PostActivationPermissionCodes.IntegrationDeadLetterReplay);

        group.MapPost("/bi/active-lines/sync", TriggerActiveLineSync)
            .WithName("TriggerActiveLineSync")
            .WithSummary("Trigger the BI active-lines synchronisation manually")
            .WithDescription(
                "TR-PAS-03: synchronisation runs on a configurable schedule, default every 60 minutes, " +
                "and must also support a manual trigger by the administrator. The run is incremental " +
                "and idempotent (TR-PAS-04).")
            .Produces<SyncRunAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequirePermission(PostActivationPermissionCodes.IntegrationSyncTrigger);

        group.MapGet("/bi/active-lines/sync/status", GetActiveLineSyncStatus)
            .WithName("GetActiveLineSyncStatus")
            .WithSummary("Report active-lines synchronisation freshness")
            .WithDescription(
                "TR-PAS-07: the last successful synchronisation timestamp must be displayed to the " +
                "administrator and monitored; two consecutive failed cycles raise an alert.")
            .Produces<ActiveLineSyncStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .RequireAuthorization();

        group.MapGet("/reconciliation", GetReconciliationReport)
            .WithName("GetReconciliationReport")
            .WithSummary("Daily portal/CRM reconciliation discrepancies")
            .WithDescription(
                "TR-INT-10: a reconciliation report comparing portal and CRM records is produced daily " +
                "and lists all discrepancies. Also serves the fallback detection of TR-ACT-19, where a " +
                "CRM state advanced without a received notification.")
            .Produces<ReconciliationDiscrepancy[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        return app;
    }

    /// <param name="targetSystem">Optional filter: Crm, Bi, Sap or Smtp.</param>
    /// <param name="outbox">Reads the dead-lettered messages.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    private static async Task<IResult> GetDeadLetterQueue(
        [FromQuery] string? targetSystem,
        IIntegrationOutbox outbox,
        CancellationToken cancellationToken,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        TargetSystem? parsedTarget = null;

        if (!string.IsNullOrWhiteSpace(targetSystem))
        {
            if (!Enum.TryParse<TargetSystem>(targetSystem, ignoreCase: true, out var value))
            {
                return Results.Problem(title: "Invalid targetSystem", detail: $"'{targetSystem}' is not Crm, Bi, Sap or Smtp.", statusCode: StatusCodes.Status400BadRequest);
            }

            parsedTarget = value;
        }

        var messages = await outbox.GetDeadLetteredAsync(parsedTarget, skip, take, cancellationToken).ConfigureAwait(false);

        return Results.Ok(messages.Select(ToDeadLetterMessage));
    }

    /// <param name="messageId">Message to re-queue.</param>
    /// <param name="outbox">Re-queues the message.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    private static async Task<IResult> ReplayDeadLetterMessage(
        [FromRoute] long messageId, IIntegrationOutbox outbox, CancellationToken cancellationToken)
    {
        try
        {
            await outbox.ReplayAsync(messageId, cancellationToken).ConfigureAwait(false);
            return Results.Accepted();
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }
    }

    /// <param name="syncService">Runs the synchronisation.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    /// <param name="fullReload">True to ignore the stored change marker and reload everything.</param>
    private static async Task<IResult> TriggerActiveLineSync(
        IActiveLineSyncService syncService, CancellationToken cancellationToken, [FromQuery] bool fullReload = false)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await syncService.SynchroniseAsync(fullReload, cancellationToken).ConfigureAwait(false);
            return Results.Accepted(value: new SyncRunAccepted(Guid.NewGuid(), startedAt));
        }
        catch (ActiveLineSyncException exception)
        {
            return Results.Problem(title: "Synchronisation failed", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> GetActiveLineSyncStatus(
        ISyncStateStore syncStateStore, IActiveLineRepository lineRepository, CancellationToken cancellationToken)
    {
        var state = await syncStateStore.GetOrCreateAsync(ActiveLineSyncService.SyncKey, cancellationToken).ConfigureAwait(false);
        var linesInScope = await lineRepository.CountAsync(cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ActiveLineSyncStatus(state.LastSuccessfulSyncAt, state.ConsecutiveFailures, linesInScope));
    }

    /// <param name="date">Report date; defaults to the most recent run.</param>
    private static IResult GetReconciliationReport([FromQuery] DateOnly? date) =>
        NotImplemented("Reconciliation reporting is not implemented at scaffold stage.");

    private static DeadLetterMessage ToDeadLetterMessage(IntegrationMessage message) =>
        new(message.MessageId, message.Direction.ToString(), message.TargetSystem.ToString(), message.InterfaceCode,
            message.RelatedPublicId, message.Attempts, message.LastError, message.CreatedAt);

    private static IResult NotImplemented(string detail) =>
        TypedResults.Problem(
            title: "Not implemented",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented);
}
