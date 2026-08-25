using Bitstream.Application.Identity.Entities;
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
    /// CRM call (TR-ACT-06, TR-DAT-01), then enqueues INT-CRM-01 on the outbox. INT-CRM-02
    /// needs the Business Partner INT-CRM-01 returns, so <c>OutboxDispatcher</c> enqueues it
    /// once that response is in hand, rather than this method enqueueing it with a placeholder.
    /// </summary>
    Task<ActivationRequest> SubmitAsync(SubmitActivationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the manual GIS verification outcome and drives the state machine (TRD 5.3).</summary>
    Task RecordGisOutcomeAsync(long requestId, bool lineAvailable, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Applies a sales order notification received on the inbound API (TR-ACT-18).</summary>
    Task ApplySalesOrderAsync(string requestPublicId, string salesOrderId, CancellationToken cancellationToken = default);

    Task<ActivationRequest?> GetByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administrator/Auditor (<c>activation.read.all</c>) searches every request; anyone else's
    /// search is silently narrowed to their own ISP — the same ownership rule
    /// <see cref="GetByPublicIdAsync"/> enforces, no different a permission needed for "own"
    /// (TR-SEC-19's reasoning applies here too). <paramref name="status"/> narrows further when given.
    /// </summary>
    Task<PagedResult<ActivationRequest>> SearchAsync(string? search, string? status, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that INT-CRM-01 and INT-CRM-02 both succeeded and moves PendingCrmSync to
    /// AwaitingGisVerification (TRD 5.3). Called by the outbox dispatcher, not by an endpoint —
    /// CRM's response is synchronous, so this does not wait for an inbound event. Keyed by the
    /// public identifier, like the other CRM-correlated methods: the dispatcher only ever knows
    /// the identifier it put on the outgoing message, never the internal primary key.
    /// </summary>
    Task MarkCrmSyncSucceededAsync(string requestPublicId, string crmCustomerId, string businessPartner, string crmTicketId, CancellationToken cancellationToken = default);

    /// <summary>Records that the outbox gave up on INT-CRM-01/02 and moves PendingCrmSync to IntegrationFailed (TR-INT-04, TR-INT-19).</summary>
    Task MarkCrmSyncFailedAsync(string requestPublicId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Administrator-initiated recovery: re-enqueues INT-CRM-01/02 and moves IntegrationFailed back to PendingCrmSync.</summary>
    Task RetryCrmSyncAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>Closes a rejected request once the ISP has been told (TRD 5.3: RejectedNoLine to Closed).</summary>
    Task CloseRejectedAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>Applies a PROVISIONING_STARTED inbound event (TRD 5.3: SalesOrderOpened to InProvisioning).</summary>
    Task StartProvisioningAsync(string requestPublicId, CancellationToken cancellationToken = default);

    /// <summary>Applies a TECHNICALLY_COMPLETED inbound event (TRD 5.3: InProvisioning to Completed).</summary>
    Task CompleteAsync(string requestPublicId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active packages, classifications and contract durations offered on the submission
    /// form right now (TR-ACT-01, TR-ACT-04, TRD 5.1) — an inactive/retired row is excluded here
    /// even though <see cref="Abstractions.Persistence.IActivationCatalogueRepository"/> itself
    /// returns every row; this is "what may be selected today", not the full history.
    /// </summary>
    Task<ActivationCatalogue> GetCatalogueAsync(CancellationToken cancellationToken = default);
}

/// <param name="Packages">Active packages only, ordered by <see cref="Package.Tier"/>.</param>
/// <param name="Classifications">Active classifications only, ordered by name.</param>
/// <param name="ContractDurations">Active contract durations only, ordered by months.</param>
public sealed record ActivationCatalogue(
    IReadOnlyList<Package> Packages,
    IReadOnlyList<ActivationClassification> Classifications,
    IReadOnlyList<ContractDuration> ContractDurations);

/// <param name="IspId">Owning ISP.</param>
/// <param name="PackageCode">From the configured catalogue (TR-ACT-01).</param>
/// <param name="LocationRaw">A map URL or a 'latitude,longitude' pair, exactly as entered (TR-ACT-02).</param>
/// <param name="Classification">From the configured list; defaults when not supplied (TR-ACT-04).</param>
/// <param name="ContractDurationMonths">12 or 24, from the configured list.</param>
/// <param name="Comments">Free text, max 2000 characters, HTML stripped (TR-ACT-05).</param>
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

    /// <summary>Null when the ticket does not exist, or when the caller is not entitled to see it (TR-SEC-19-style).</summary>
    Task<ComplaintTicket?> GetByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketComment>> GetCommentsAsync(long ticketId, CancellationToken cancellationToken = default);
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

/// <param name="Dataset">Which report dataset to export.</param>
/// <param name="Format">CSV or XLSX (TR-REP-01).</param>
/// <param name="Filters">Applied filter set; echoed into the file header (TR-REP-06).</param>
public sealed record ExportRequest(
    string Dataset,
    string Format,
    IReadOnlyDictionary<string, string> Filters);

public sealed record ExportStatus(Guid ExportId, string State, int RowCount, string? DownloadToken);

/// <summary>
/// Login/2FA/session orchestration (TRD 4.1) moved to <c>Bitstream.Web.Controllers.AuthController</c>:
/// it is now <c>SignInManager&lt;User&gt;</c>-driven, which needs <c>HttpContext</c> to read/write
/// the authentication cookie — an HTTP concern this layer has stayed decoupled from all along
/// (the same reason <c>HttpCurrentUserContext</c> lives in <c>Bitstream.Web</c>, not here).
/// </summary>

/// <summary>
/// ISP and user administration, Administrator role only (TR-SEC-09 to TR-SEC-16).
/// <para>
/// The read methods enforce ownership the same way for every caller: an Administrator or
/// Auditor (holding <c>isp.read.all</c>) may read any ISP or user; anyone else may read only
/// their own ISP or their own user record. A request for someone else's returns null exactly as
/// if the record did not exist — TR-SEC-19 requires a not-found response, not a forbidden one,
/// specifically so that the response cannot be used to confirm another ISP's existence.
/// </para>
/// </summary>
public interface IAdministrationService
{
    Task<Isp> CreateIspAsync(CreateIspRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when the ISP does not exist, or when the caller is not entitled to see it (TR-SEC-19).</summary>
    Task<Isp?> GetIspAsync(long ispId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administrator/Auditor (<c>isp.read.all</c>) searches every ISP; anyone else's search is
    /// silently narrowed to their own ISP, the same ownership rule <see cref="GetIspAsync"/>
    /// enforces — there being nothing to browse is not distinguished from lacking the
    /// permission (TR-SEC-19's reasoning applies here too).
    /// </summary>
    Task<PagedResult<Isp>> SearchIspsAsync(string? search, string? status, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="Identity.AdministrationValidationException"/> when the ISP does not exist, a field is invalid, or the new NIPT collides with another ISP.</summary>
    Task<Isp> UpdateIspAsync(long ispId, UpdateIspRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locking cascades to every currently-active user of the ISP and revokes their sessions
    /// immediately (TR-SEC-13, TR-SEC-07). Unlocking the ISP does not reciprocally unlock its
    /// users — each stays exactly as an administrator last set it, so unlocking never silently
    /// reactivates an account that was locked for an unrelated reason.
    /// </summary>
    Task SetIspStatusAsync(long ispId, IspStatus status, CancellationToken cancellationToken = default);

    Task<User> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>Null when the user does not exist, or when the caller is not entitled to see it. Self and Administrator/Auditor only.</summary>
    Task<User?> GetUserAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same ownership narrowing as <see cref="SearchIspsAsync"/>, applied to users instead of ISPs.
    /// <paramref name="roleName"/> and <paramref name="status"/> ("Active"/"Locked") narrow the
    /// grid further when given.
    /// </summary>
    Task<PagedResult<User>> SearchUsersAsync(string? search, string? roleName, string? status, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Edits a user's profile fields (not the password or the status). Same validation as <see cref="CreateUserAsync"/>.</summary>
    Task<User> UpdateUserAsync(long userId, UpdateUserRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administrator-set password reset: validated against the same policy as
    /// <see cref="CreateUserAsync"/> (TR-SEC-03), recorded to password history, and followed by an
    /// immediate invalidation of the user's other sessions (TR-SEC-07, via
    /// <c>UserManager.ResetPasswordAsync</c>'s own security-stamp rotation) so a session opened
    /// under the old password cannot outlive the change.
    /// </summary>
    Task ChangeUserPasswordAsync(long userId, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks or unlocks a user (TR-SEC-12) via <c>UserManager.SetLockoutEndDateAsync</c> —
    /// "locked" is not <see cref="UserStatus"/> state, it is a derived condition
    /// (<c>UserManager.IsLockedOutAsync</c>). Locking invalidates the user's sessions immediately
    /// (TR-SEC-07).
    /// </summary>
    Task SetUserLockedAsync(long userId, bool locked, CancellationToken cancellationToken = default);
}

/// <param name="Items">At most <c>take</c> rows, most recently created first.</param>
/// <param name="TotalCount">Total rows matching the search, ignoring paging — for the caller to compute page count.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

/// <param name="Name">ISP name.</param>
/// <param name="Nipt">Albanian tax identification number.</param>
/// <param name="ContactPerson">Primary contact's name.</param>
/// <param name="ContactEmail">Primary contact's email.</param>
/// <param name="ContactMobile">E.164 format (TR-SEC-14, TR-SEC-15).</param>
/// <param name="CrmBpReference">CRM Business Partner reference; verified against CRM before activation per TR-SEC-16 (Should — CRM contract is TRD 11.4 open item 1, so this is recorded but not yet cross-checked).</param>
public sealed record CreateIspRequest(
    string Name,
    string Nipt,
    string ContactPerson,
    string ContactEmail,
    string ContactMobile,
    string CrmBpReference);

/// <summary>Same fields as <see cref="CreateIspRequest"/> — everything but status is editable (TR-SEC-15).</summary>
public sealed record UpdateIspRequest(
    string Name,
    string Nipt,
    string ContactPerson,
    string ContactEmail,
    string ContactMobile,
    string CrmBpReference);

/// <param name="IspId">Owning ISP, or null for an internal user (Administrator, Service Desk, Auditor) (TR-SEC-14).</param>
/// <param name="FullName">User's full name.</param>
/// <param name="Email">RFC-compliant, unique across the portal.</param>
/// <param name="Mobile">E.164 format.</param>
/// <param name="RoleName">One of the seeded roles: Administrator, IspUser, ServiceDesk, Auditor.</param>
/// <param name="InitialPassword">Must satisfy the configured password policy (TR-SEC-03); the user is expected to change it, though self-service change is not yet built (see docs/open-items.md).</param>
public sealed record CreateUserRequest(
    long? IspId,
    string FullName,
    string Email,
    string Mobile,
    string RoleName,
    string InitialPassword);

/// <param name="IspId">Owning ISP, or null for an internal user (TR-SEC-14).</param>
/// <param name="FullName">User's full name.</param>
/// <param name="Email">RFC-compliant, unique across the portal (excluding the user being edited).</param>
/// <param name="Mobile">E.164 format.</param>
/// <param name="RoleName">One of the seeded roles: Administrator, IspUser, ServiceDesk, Auditor.</param>
public sealed record UpdateUserRequest(
    long? IspId,
    string FullName,
    string Email,
    string Mobile,
    string RoleName);
