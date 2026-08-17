using Bitstream.Api.Contracts;
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
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapPost("/integration/dead-letter/{messageId:long}/replay", ReplayDeadLetter)
            .WithName("ReplayDeadLetterMessage")
            .WithSummary("Re-queue a dead-lettered integration message")
            .WithDescription(
                "TR-INT-05: replay must not lose or duplicate data. The message keeps its original " +
                "idempotency key, so the receiver deduplicates a message that in fact arrived (TR-INT-03).")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

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
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapGet("/bi/active-lines/sync/status", GetActiveLineSyncStatus)
            .WithName("GetActiveLineSyncStatus")
            .WithSummary("Report active-lines synchronisation freshness")
            .WithDescription(
                "TR-PAS-07: the last successful synchronisation timestamp must be displayed to the " +
                "administrator and monitored; two consecutive failed cycles raise an alert.")
            .Produces<ActiveLineSyncStatus>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status501NotImplemented);

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
    /// <param name="skip">Rows to skip.</param>
    /// <param name="take">Rows to return.</param>
    private static IResult GetDeadLetterQueue(
        [FromQuery] string? targetSystem,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50) =>
        NotImplemented("Dead-letter inspection is not implemented at scaffold stage.");

    /// <param name="messageId">Message to re-queue.</param>
    private static IResult ReplayDeadLetter([FromRoute] long messageId) =>
        NotImplemented("Dead-letter replay is not implemented at scaffold stage.");

    /// <param name="fullReload">True to ignore the stored change marker and reload everything.</param>
    private static IResult TriggerActiveLineSync([FromQuery] bool fullReload = false) =>
        NotImplemented("Active-lines synchronisation is not implemented at scaffold stage.");

    private static IResult GetActiveLineSyncStatus() =>
        NotImplemented("Synchronisation status is not implemented at scaffold stage.");

    /// <param name="date">Report date; defaults to the most recent run.</param>
    private static IResult GetReconciliationReport([FromQuery] DateOnly? date) =>
        NotImplemented("Reconciliation reporting is not implemented at scaffold stage.");

    private static IResult NotImplemented(string detail) =>
        TypedResults.Problem(
            title: "Not implemented",
            detail: detail,
            statusCode: StatusCodes.Status501NotImplemented);
}
