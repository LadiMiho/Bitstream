using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IComplaintTicketRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class ComplaintTicketRepository : IComplaintTicketRepository
{
    private readonly BitstreamDbContext _dbContext;

    public ComplaintTicketRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<ComplaintTicket?> FindByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        _dbContext.ComplaintTickets.FirstOrDefaultAsync(ticket => ticket.TicketId == ticketId, cancellationToken);

    public Task<ComplaintTicket?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        _dbContext.ComplaintTickets.FirstOrDefaultAsync(ticket => ticket.PublicId == publicId, cancellationToken);

    public async Task AddAsync(ComplaintTicket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        await _dbContext.ComplaintTickets.AddAsync(ticket, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ComplaintTicket>> SearchAsync(
        long? ispId,
        string? status,
        DateTimeOffset? createdFrom,
        DateTimeOffset? createdTo,
        string? categoryL1,
        long? lineId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ComplaintTickets.AsQueryable();

        if (ispId is { } isp)
        {
            query = query.Where(ticket => ticket.IspId == isp);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(ticket => ticket.Status == status);
        }

        if (createdFrom is { } from)
        {
            query = query.Where(ticket => ticket.OpenedAt >= from);
        }

        if (createdTo is { } to)
        {
            query = query.Where(ticket => ticket.OpenedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(categoryL1))
        {
            query = query.Where(ticket => ticket.CategoryL1 == categoryL1);
        }

        if (lineId is { } line)
        {
            query = query.Where(ticket => ticket.LineId == line);
        }

        return await query
            .OrderByDescending(ticket => ticket.OpenedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ComplaintTicket>> FindAwaitingConfirmationAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.ComplaintTickets
            .Where(ticket => ticket.ConfirmationDueAt != null && ticket.ClosureDecision == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddCommentAsync(TicketComment comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comment);

        await _dbContext.TicketComments.AddAsync(comment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TicketComment>> GetCommentsAsync(long ticketId, CancellationToken cancellationToken = default) =>
        await _dbContext.TicketComments
            .Where(comment => comment.TicketId == ticketId)
            .OrderBy(comment => comment.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
