using System.Text.Json.Serialization;

namespace Bitstream.Api.Contracts;

/// <summary>Dead-lettered integration message, for administrator inspection (TR-INT-05).</summary>
/// <param name="MessageId">Portal message identifier.</param>
/// <param name="Direction">Outbound or Inbound.</param>
/// <param name="TargetSystem">Crm, Bi, Sap or Smtp.</param>
/// <param name="InterfaceCode">Interface code from TRD 7.1, e.g. INT-CRM-02.</param>
/// <param name="RelatedPublicId">Request or ticket the message belongs to.</param>
/// <param name="Attempts">Dispatch attempts made before dead-lettering.</param>
/// <param name="LastError">Last recorded failure. Sensitive fields are masked (TR-INT-09).</param>
/// <param name="CreatedAt">When the message was first enqueued.</param>
public sealed record DeadLetterMessage(
    [property: JsonPropertyName("messageId")] long MessageId,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("targetSystem")] string TargetSystem,
    [property: JsonPropertyName("interfaceCode")] string InterfaceCode,
    [property: JsonPropertyName("relatedPublicId")] string? RelatedPublicId,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt);

/// <summary>State of the BI active-lines synchronisation (TR-PAS-07).</summary>
/// <param name="LastSuccessfulSyncAt">Timestamp shown to the administrator and monitored.</param>
/// <param name="ConsecutiveFailures">Failures since the last success; an alert is raised above the configured threshold.</param>
/// <param name="LinesInScope">Rows currently held in the active-lines projection.</param>
public sealed record ActiveLineSyncStatus(
    [property: JsonPropertyName("lastSuccessfulSyncAt")] DateTimeOffset? LastSuccessfulSyncAt,
    [property: JsonPropertyName("consecutiveFailures")] int ConsecutiveFailures,
    [property: JsonPropertyName("linesInScope")] int LinesInScope);

/// <summary>Result of triggering a synchronisation manually (TR-PAS-03).</summary>
/// <param name="RunId">Identifier of the started run.</param>
/// <param name="StartedAt">When the run was accepted.</param>
public sealed record SyncRunAccepted(
    [property: JsonPropertyName("runId")] Guid RunId,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);

/// <summary>One discrepancy in the daily portal/CRM reconciliation report (TR-INT-10).</summary>
/// <param name="PublicId">Portal identifier of the record.</param>
/// <param name="EntityType">ActivationRequest or ComplaintTicket.</param>
/// <param name="PortalState">State held by the portal.</param>
/// <param name="CrmState">State reported by CRM.</param>
/// <param name="DetectedAt">When the discrepancy was detected.</param>
public sealed record ReconciliationDiscrepancy(
    [property: JsonPropertyName("publicId")] string PublicId,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("portalState")] string PortalState,
    [property: JsonPropertyName("crmState")] string? CrmState,
    [property: JsonPropertyName("detectedAt")] DateTimeOffset DetectedAt);
