namespace Bitstream.Application.Abstractions.Integration;

/// <summary>
/// Envelope carried by every outbound message (TR-INT-02).
/// </summary>
/// <param name="MessageId">Unique identifier of this message.</param>
/// <param name="CorrelationId">Correlation ID of the originating request (TR-ARC-04).</param>
/// <param name="IdempotencyKey">Deduplication key for the receiver (TR-INT-03).</param>
/// <param name="OccurredAt">UTC timestamp of the originating event.</param>
public sealed record IntegrationEnvelope(
    Guid MessageId,
    string CorrelationId,
    string IdempotencyKey,
    DateTimeOffset OccurredAt);

// --- INT-CRM-01 Create Customer -------------------------------------------------

/// <param name="RequestPublicId">Portal request identifier, carried on every message (TR-DAT-04).</param>
public sealed record CreateCrmCustomerCommand(
    IntegrationEnvelope Envelope,
    string RequestPublicId,
    string IspName,
    string IspNipt,
    string ContactPerson,
    string ContactEmail,
    string ContactMobile);

/// <param name="BusinessPartner">BP assigned by CRM, required for the subsequent ticket (TR-ACT-08).</param>
public sealed record CreateCrmCustomerResult(string CrmCustomerId, string BusinessPartner);

// --- INT-CRM-02 Create Activation Ticket ----------------------------------------

/// <param name="CrmCustomerId">The CRM-side customer ID INT-CRM-01 returned, carried forward so the caller does not have to look its own earlier response back up.</param>
public sealed record CreateActivationTicketCommand(
    IntegrationEnvelope Envelope,
    string RequestPublicId,
    string CrmCustomerId,
    string BusinessPartner,
    string Classification,
    string PackageCode,
    int ContractDurationMonths,
    string LocationRaw,
    decimal LocationLat,
    decimal LocationLng,
    string? Comments);

public sealed record CreateCrmTicketResult(string CrmTicketId);

// --- INT-CRM-04 Create Complaint Ticket -----------------------------------------

public sealed record CreateComplaintTicketCommand(
    IntegrationEnvelope Envelope,
    string TicketPublicId,
    string BusinessPartner,
    string ContractId,
    string SubscriberReference,
    string CategoryL1,
    string CategoryL2,
    string CategoryL3,
    string Description);

// --- INT-CRM-06 Comment Replication ---------------------------------------------

public sealed record ReplicateCommentCommand(
    IntegrationEnvelope Envelope,
    string TicketPublicId,
    string CrmTicketId,
    string AuthorDisplayName,
    string AuthorType,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record ReplicateCommentResult(string CrmCommentId);

// --- INT-CRM-08 Closure Decision -------------------------------------------------

/// <param name="Decision">CONFIRM or REJECT.</param>
/// <param name="SystemInitiated">True for auto-confirmation; carries reason and elapsed period (TR-PAS-21d).</param>
public sealed record ClosureDecisionCommand(
    IntegrationEnvelope Envelope,
    string TicketPublicId,
    string CrmTicketId,
    string Decision,
    bool SystemInitiated,
    string? SystemReason,
    TimeSpan? ElapsedSinceClearingCode);

public sealed record ClosureDecisionResult(string CrmTicketStatus);

// --- INT-CRM-09 Service Change ---------------------------------------------------

public sealed record ServiceChangeCommand(
    IntegrationEnvelope Envelope,
    string ChangePublicId,
    string ContractId,
    string ChangeType,
    string PackageAsIs,
    string? PackageToBe,
    DateOnly? RequestedTerminationDate);

public sealed record ServiceChangeResult(string CrmReference);

// --- INT-BI-01 Active Lines Sync -------------------------------------------------

/// <param name="ChangeMarker">Watermark returned by the previous run; null for a full load (TR-PAS-04).</param>
public sealed record ActiveLinesQuery(string? ChangeMarker, int PageSize, int PageNumber);

public sealed record ActiveLineRecord(
    string IspCrmBpReference,
    string ContractId,
    string SubscriberReference,
    string Technology,
    string PackageCode,
    string Status,
    string? ChangeMarker);

public sealed record ActiveLinesPage(
    IReadOnlyList<ActiveLineRecord> Lines,
    string? NextChangeMarker,
    bool HasMore);

// --- INT-BI-02 Reporting Extract -------------------------------------------------

public sealed record ReportingExtractCommand(
    IntegrationEnvelope Envelope,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc);

// --- INT-SAP-01 Financial Code ---------------------------------------------------

/// <summary>
/// Direction and trigger are undecided (TRD 11.4 open item 5); the port exists so that the
/// data model and the flow do not have to change once the decision is taken (TR-INT-11/12).
/// </summary>
public sealed record FinancialCodeQuery(string RequestPublicId, string BusinessPartner);

public sealed record FinancialCodeResult(string? FinancialCode);

// --- INT-MAIL-01 Email Dispatch --------------------------------------------------

public sealed record EmailMessage(
    IntegrationEnvelope Envelope,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    string Subject,
    string HtmlBody,
    string PlainTextBody);
