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
