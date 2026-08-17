using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;

namespace Bitstream.Application.Services;

/*
 * Application service surface (TRD 2.2 "Logical Components").
 *
 * Interfaces only at scaffold stage. Implementations belong in this project, in
 * Services/<Module>/, and may depend only on the abstractions in
 * Bitstream.Application.Abstractions — never on an adapter, an HttpClient or a DbContext.
 */

/// <summary>Request Service — activation request lifecycle (TRD 5).</summary>
public interface IActivationRequestService
{
    /// <summary>
    /// Validates, persists with status Submitted and issues the public identifier before any
    /// CRM call (TR-ACT-06, TR-DAT-01), then enqueues INT-CRM-01 and INT-CRM-02 on the outbox.
    /// </summary>
    Task<ActivationRequest> SubmitAsync(SubmitActivationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the manual GIS verification outcome and drives the state machine (TRD 5.3).</summary>
    Task RecordGisOutcomeAsync(long requestId, bool lineAvailable, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Applies a sales order notification received on the inbound API (TR-ACT-18).</summary>
    Task ApplySalesOrderAsync(string requestPublicId, string salesOrderId, CancellationToken cancellationToken = default);

    Task<ActivationRequest?> GetByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);
}

/// <param name="ContractDurationMonths">12 or 24, from the configured list.</param>
public sealed record SubmitActivationRequest(
    long IspId,
    string PackageCode,
    string LocationRaw,
    string Classification,
    int ContractDurationMonths,
    string? Comments);

/// <summary>Ticket Service — complaint ticket lifecycle (TRD 6.2, 6.6).</summary>
public interface IComplaintTicketService
{
    Task<ComplaintTicket> CreateAsync(CreateComplaintTicket ticket, CancellationToken cancellationToken = default);

    /// <summary>Adds a comment and enqueues replication to CRM (TR-PAS-26, TR-PAS-28).</summary>
    Task<TicketComment> AddCommentAsync(long ticketId, string body, CancellationToken cancellationToken = default);

    /// <summary>Scoped server-side to the caller's ISP (TR-SEC-18, TR-PAS-06).</summary>
    Task<IReadOnlyList<ComplaintTicket>> SearchAsync(ComplaintTicketFilter filter, CancellationToken cancellationToken = default);
}

public sealed record CreateComplaintTicket(
    long IspId,
    long LineId,
    string CategoryL1,
    string CategoryL2,
    string CategoryL3,
    string Description);

public sealed record ComplaintTicketFilter(
    long? IspId,
    string? Status,
    DateTimeOffset? CreatedFrom,
    DateTimeOffset? CreatedTo,
    string? CategoryL1,
    long? LineId,
    int Skip,
    int Take);

/// <summary>Closure handshake and unanswered-closure handling (TRD 6.4, 6.5).</summary>
public interface ITicketClosureService
{
    /// <summary>Stores the clearing code, enters Pending ISP Confirmation and starts the clock (TR-PAS-18).</summary>
    Task ApplyClearingCodeAsync(string ticketPublicId, string clearingCode, string? clearingText, CancellationToken cancellationToken = default);

    /// <summary>Records Confirm or No and transmits it to CRM (TR-PAS-19, TR-PAS-20).</summary>
    Task RecordIspDecisionAsync(long ticketId, ClosureDecision decision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Auto-confirmation sweep (TR-PAS-21a to TR-PAS-21e). A persisted ISP decision always
    /// takes precedence over a concurrent sweep.
    /// </summary>
    Task RunAutoConfirmationSweepAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a linked follow-up ticket within the challenge window (TR-PAS-21f).</summary>
    Task<ComplaintTicket> RaiseFollowUpAsync(long originalTicketId, string description, CancellationToken cancellationToken = default);
}

/// <summary>Service status management — upgrade, downgrade, termination (TRD 6.8).</summary>
public interface IServiceChangeRequestService
{
    Task<ServiceChangeRequest> SubmitAsync(
        long lineId,
        ServiceChangeType changeType,
        string? packageToBe,
        DateOnly? requestedTerminationDate,
        CancellationToken cancellationToken = default);

    /// <summary>Target packages valid for the change type, excluding the current one (TR-PAS-35).</summary>
    Task<IReadOnlyList<string>> GetEligibleTargetPackagesAsync(
        long lineId,
        ServiceChangeType changeType,
        CancellationToken cancellationToken = default);
}

/// <summary>Scheduled and manual synchronisation of the BI active-lines table (TRD 6.1).</summary>
public interface IActiveLineSyncService
{
    /// <summary>Incremental and idempotent (TR-PAS-04). Returns the number of rows touched.</summary>
    Task<int> SynchroniseAsync(bool fullReload, CancellationToken cancellationToken = default);

    /// <summary>Last successful synchronisation, displayed to the administrator (TR-PAS-07).</summary>
    Task<DateTimeOffset?> GetLastSuccessfulSyncAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interpretation of CRM-originated events received on the inbound API (TRD 7.3.2).
/// The endpoint persists and acknowledges; this service runs asynchronously afterwards
/// so that CRM is never held open on portal-side work.
/// </summary>
public interface IInboundEventService
{
    /// <summary>Applies one already-persisted raw event, honouring dedup and ordering (TR-INT-25).</summary>
    Task ApplyAsync(long integrationMessageId, CancellationToken cancellationToken = default);

    /// <summary>Reprocesses events for a ticket or time window during recovery (TR-INT-31).</summary>
    Task ReplayAsync(string? ticketPublicId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken = default);
}

/// <summary>Notification Service — template rendering and dispatch logging (TRD 8).</summary>
public interface INotificationService
{
    /// <summary>
    /// Renders a template and queues the mail. Suppression rules of TRD 6.3 are applied by
    /// the caller's event interpretation, not here (TR-PAS-13, TR-INT-28).
    /// </summary>
    Task<Notification> QueueAsync(
        string templateCode,
        IReadOnlyDictionary<string, string> variables,
        string relatedEntityType,
        long? relatedEntityId,
        CancellationToken cancellationToken = default);
}

/// <summary>Reporting Service — filtered extraction and export (TRD 9).</summary>
public interface IReportingService
{
    /// <summary>
    /// Starts an export. Exports above the configured row threshold run asynchronously and
    /// are collected later (TR-REP-07). Every export is audited (TR-REP-08).
    /// </summary>
    Task<Guid> RequestExportAsync(ExportRequest request, CancellationToken cancellationToken = default);

    Task<ExportStatus> GetExportStatusAsync(Guid exportId, CancellationToken cancellationToken = default);
}

/// <param name="Format">CSV or XLSX (TR-REP-01).</param>
/// <param name="Filters">Applied filter set; echoed into the file header (TR-REP-06).</param>
public sealed record ExportRequest(
    string Dataset,
    string Format,
    IReadOnlyDictionary<string, string> Filters);

public sealed record ExportStatus(Guid ExportId, string State, int RowCount, string? DownloadToken);

/// <summary>Identity &amp; Access Service — authentication, 2FA, administration (TRD 4).</summary>
public interface IIdentityService
{
    /// <summary>First factor. Never reveals whether the account exists; locks at 5 failures (TR-SEC-06).</summary>
    Task<AuthenticationChallenge> AuthenticateAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Second factor; the code is single-use and valid for at most 5 minutes (TR-SEC-04).</summary>
    Task<AuthenticationResult> CompleteSecondFactorAsync(string challengeId, string code, CancellationToken cancellationToken = default);

    Task SignOutAsync(string sessionId, CancellationToken cancellationToken = default);
}

public sealed record AuthenticationChallenge(string ChallengeId, string SecondFactorChannel, DateTimeOffset ExpiresAt);

public sealed record AuthenticationResult(bool Succeeded, string? SessionToken, DateTimeOffset? ExpiresAt);

/// <summary>ISP and user administration, Administrator role only (TR-SEC-09 to TR-SEC-16).</summary>
public interface IAdministrationService
{
    Task<Isp> CreateIspAsync(Isp isp, CancellationToken cancellationToken = default);

    /// <summary>Locking an ISP locks all of its users (TR-SEC-13).</summary>
    Task SetIspStatusAsync(long ispId, IspStatus status, CancellationToken cancellationToken = default);

    Task<User> CreateUserAsync(User user, CancellationToken cancellationToken = default);

    Task SetUserStatusAsync(long userId, UserStatus status, CancellationToken cancellationToken = default);
}
