using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services;
using Bitstream.Application.Services.PostActivation;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Bitstream.Hosting.Configuration;
using Bitstream.Web.Contracts;
using Bitstream.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Bitstream.Web.Controllers;

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
/// <para>No page consumes these actions yet — no Operations screen exists — but the JSON
/// contract stays in place for when one is built, and tests already exercise it.</para>
/// </summary>
[Route("Operations")]
public sealed class OperationsController : Controller
{
    [HttpGet("integration/dead-letter")]
    [RequireJsonPermission(PostActivationPermissionCodes.IntegrationDeadLetterRead)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> GetDeadLetterQueue(
        [FromServices] IIntegrationOutbox outbox,
        CancellationToken cancellationToken,
        [FromQuery] string? targetSystem,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50)
    {
        TargetSystem? parsedTarget = null;

        if (!string.IsNullOrWhiteSpace(targetSystem))
        {
            if (!Enum.TryParse<TargetSystem>(targetSystem, ignoreCase: true, out var value))
            {
                return Problem(title: "Invalid targetSystem", detail: $"'{targetSystem}' is not Crm, Bi, Sap or Smtp.", statusCode: StatusCodes.Status400BadRequest);
            }

            parsedTarget = value;
        }

        var messages = await outbox.GetDeadLetteredAsync(parsedTarget, skip, take, cancellationToken).ConfigureAwait(false);

        return Ok(messages.Select(ToDeadLetterMessage));
    }

    [HttpPost("integration/dead-letter/{messageId:long}/replay")]
    [RequireJsonPermission(PostActivationPermissionCodes.IntegrationDeadLetterReplay)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> ReplayDeadLetterMessage(
        long messageId, [FromServices] IIntegrationOutbox outbox, CancellationToken cancellationToken)
    {
        try
        {
            await outbox.ReplayAsync(messageId, cancellationToken).ConfigureAwait(false);
            return Accepted();
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost("bi/active-lines/sync")]
    [RequireJsonPermission(PostActivationPermissionCodes.IntegrationSyncTrigger)]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> TriggerActiveLineSync(
        [FromServices] IActiveLineSyncService syncService, CancellationToken cancellationToken, [FromQuery] bool fullReload = false)
    {
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await syncService.SynchroniseAsync(fullReload, cancellationToken).ConfigureAwait(false);
            return Accepted(value: new SyncRunAccepted(Guid.NewGuid(), startedAt));
        }
        catch (ActiveLineSyncException exception)
        {
            return Problem(title: "Synchronisation failed", detail: exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    [HttpGet("bi/active-lines/sync/status")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public async Task<IActionResult> GetActiveLineSyncStatus(
        [FromServices] ISyncStateStore syncStateStore, [FromServices] IActiveLineRepository lineRepository, CancellationToken cancellationToken)
    {
        var state = await syncStateStore.GetOrCreateAsync(ActiveLineSyncService.SyncKey, cancellationToken).ConfigureAwait(false);
        var linesInScope = await lineRepository.CountAsync(cancellationToken).ConfigureAwait(false);

        return Ok(new ActiveLineSyncStatus(state.LastSuccessfulSyncAt, state.ConsecutiveFailures, linesInScope));
    }

    /// <summary>
    /// Closes a pre-existing gap found while moving this off /api/v1/ops: this action had no
    /// authorization requirement at all before. TR-SEC-17 expects every screen to require a
    /// signed-in session at minimum, matching its sibling <see cref="GetActiveLineSyncStatus"/>.
    /// </summary>
    [HttpGet("reconciliation")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Administration)]
    public IActionResult GetReconciliationReport([FromQuery] DateOnly? date) =>
        NotImplemented("Reconciliation reporting is not implemented at scaffold stage.");

    private static DeadLetterMessage ToDeadLetterMessage(IntegrationMessage message) =>
        new(message.MessageId, message.Direction.ToString(), message.TargetSystem.ToString(), message.InterfaceCode,
            message.RelatedPublicId, message.Attempts, message.LastError, message.CreatedAt);

    private ObjectResult NotImplemented(string detail) =>
        Problem(title: "Not implemented", detail: detail, statusCode: StatusCodes.Status501NotImplemented);
}
