using Bitstream.Domain.Entities;

namespace Bitstream.Application.Abstractions.Persistence;

/// <summary>Data access for the BI active-lines projection (TRD 6.1).</summary>
public interface IActiveLineRepository
{
    Task<ActiveLine?> FindByIdAsync(long lineId, CancellationToken cancellationToken = default);

    /// <summary>The upsert key that makes synchronisation idempotent (TR-PAS-04).</summary>
    Task<ActiveLine?> FindByIspAndContractAsync(long ispId, string contractId, CancellationToken cancellationToken = default);

    Task AddAsync(ActiveLine line, CancellationToken cancellationToken = default);

    /// <summary>Rows in scope for the line dropdown / sync status (TR-PAS-05, TR-PAS-07).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

/// <summary>Data access for the complaint ticket lifecycle (TRD 6.2 to 6.7).</summary>
public interface IComplaintTicketRepository
{
    Task<ComplaintTicket?> FindByIdAsync(long ticketId, CancellationToken cancellationToken = default);

    Task<ComplaintTicket?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>Looked up by CRM's own ticket number, as CRM may address its inbound events.</summary>
    Task<ComplaintTicket?> FindByCrmTicketIdAsync(string crmTicketId, CancellationToken cancellationToken = default);

    Task AddAsync(ComplaintTicket ticket, CancellationToken cancellationToken = default);

    /// <summary>Ownership-scoped by the caller; server-side filtered and paged (TR-PAS-06, TR-PAS-31/32).</summary>
    Task<IReadOnlyList<ComplaintTicket>> SearchAsync(
        long? ispId,
        string? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        string? categoryL1,
        long? lineId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Tickets awaiting an ISP decision — the auto-confirmation sweep's working set (TR-PAS-21).</summary>
    Task<IReadOnlyList<ComplaintTicket>> FindAwaitingConfirmationAsync(CancellationToken cancellationToken = default);

    Task AddCommentAsync(TicketComment comment, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TicketComment>> GetCommentsAsync(long ticketId, CancellationToken cancellationToken = default);
}

/// <summary>Data access for upgrade/downgrade/termination requests (TRD 6.8).</summary>
public interface IServiceChangeRequestRepository
{
    Task<ServiceChangeRequest?> FindByIdAsync(long changeId, CancellationToken cancellationToken = default);

    Task<ServiceChangeRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    Task AddAsync(ServiceChangeRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Persists queued notifications (TRD 8). Dispatch itself is <see cref="Integration.IEmailGateway"/>'s job.</summary>
public interface INotificationRepository
{
    Task AddAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>One row per named scheduled sync job (TR-PAS-03, TR-PAS-07).</summary>
public interface ISyncStateStore
{
    Task<SyncState> GetOrCreateAsync(string syncKey, CancellationToken cancellationToken = default);

    Task SaveAsync(SyncState state, CancellationToken cancellationToken = default);
}

/// <summary>Data access for the three-level post-activation classification (portal.SupportClassification).</summary>
public interface ISupportClassificationRepository
{
    /// <summary>Every node, all levels, tracked for update.</summary>
    Task<IReadOnlyList<SupportClassification>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<SupportClassification?> FindByIdAsync(long classificationId, CancellationToken cancellationToken = default);

    Task<SupportClassification?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Stages a new node; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task AddAsync(SupportClassification classification, CancellationToken cancellationToken = default);
}

/// <summary>Data access for post-activation requests (portal.PostActivationRequest) and BI's phone numbers (portal.BiPhoneNumber).</summary>
public interface IPostActivationRequestRepository
{
    /// <summary>
    /// BI's phone numbers, sorted. With <paramref name="ispTicketCode"/>, only those whose prefix
    /// (the text before the first <c>_</c>) equals it, ignoring case; without, all of them.
    /// </summary>
    Task<IReadOnlyList<string>> GetPhoneNumbersAsync(string? ispTicketCode, CancellationToken cancellationToken = default);

    Task<bool> PhoneNumberExistsAsync(string phonenumber, CancellationToken cancellationToken = default);

    Task<PostActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>The request whose CRM ticket (the ticket number CRM returned) is <paramref name="crmTicketId"/>.</summary>
    Task<PostActivationRequest?> FindByCrmTicketIdAsync(string crmTicketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The grid's search and filters, newest first, with the ISP loaded. The ISP filter is also
    /// the ownership scope: the service forces it to the caller's own ISP for anyone without
    /// <c>ticket.read.all</c>.
    /// </summary>
    Task<(IReadOnlyList<PostActivationRequest> Items, int TotalCount)> SearchAsync(
        PostActivationRequestSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Every outbox message about the request (its public id), oldest first, for the View drawer.</summary>
    Task<IReadOnlyList<IntegrationMessage>> GetMessagesAsync(string publicId, CancellationToken cancellationToken = default);

    /// <summary>Stages a new request; persisted by <see cref="IUnitOfWork.SaveChangesAsync"/>.</summary>
    Task AddAsync(PostActivationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The Post-Activation grid's search box and Filters popup; every criterion is optional and they combine with AND.</summary>
/// <param name="Search">Case-insensitive substring of the request ID or the phone number.</param>
/// <param name="Status">Exact <see cref="PostActivationRequestStatus"/> code.</param>
/// <param name="IspId">One ISP's requests.</param>
/// <param name="Classification1Code">Request type (level 1).</param>
/// <param name="Classification2Code">Category (level 2).</param>
/// <param name="SubmittedFrom">Submitted at or after this instant.</param>
/// <param name="SubmittedBefore">Submitted before this instant (the day after the "to" date).</param>
public sealed record PostActivationRequestSearchFilter(
    string? Search = null,
    string? Status = null,
    long? IspId = null,
    string? Classification1Code = null,
    string? Classification2Code = null,
    DateTimeOffset? SubmittedFrom = null,
    DateTimeOffset? SubmittedBefore = null);
