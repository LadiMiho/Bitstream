using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services;
using Bitstream.Domain.Entities;

namespace Bitstream.Api.Tests.PostActivation;

/*
 * Hand-written test doubles for the post-activation module's ports, matching the style already
 * used in Identity/Fakes.cs and Activation/Fakes.cs rather than adding a mocking framework.
 */

public sealed class FakeComplaintTicketRepository : IComplaintTicketRepository
{
    public Dictionary<long, ComplaintTicket> Tickets { get; } = [];

    public List<TicketComment> Comments { get; } = [];

    private long _nextTicketId = 1;

    private long _nextCommentId = 1;

    public Task<ComplaintTicket?> FindByIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tickets.GetValueOrDefault(ticketId));

    public Task<ComplaintTicket?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tickets.Values.FirstOrDefault(t => t.PublicId == publicId));

    public Task AddAsync(ComplaintTicket ticket, CancellationToken cancellationToken = default)
    {
        if (ticket.TicketId == 0)
        {
            ticket.TicketId = _nextTicketId++;
        }

        Tickets[ticket.TicketId] = ticket;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ComplaintTicket>> SearchAsync(
        long? ispId, string? status, DateTimeOffset? createdFrom, DateTimeOffset? createdTo, string? categoryL1, long? lineId,
        int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = Tickets.Values.AsEnumerable();

        if (ispId is { } isp)
        {
            query = query.Where(t => t.IspId == isp);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(t => t.Status == status);
        }

        if (lineId is { } line)
        {
            query = query.Where(t => t.LineId == line);
        }

        return Task.FromResult<IReadOnlyList<ComplaintTicket>>([.. query.OrderByDescending(t => t.OpenedAt).Skip(skip).Take(take)]);
    }

    public Task<IReadOnlyList<ComplaintTicket>> FindAwaitingConfirmationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ComplaintTicket>>(
            [.. Tickets.Values.Where(t => t.ConfirmationDueAt != null && t.ClosureDecision == null)]);

    public Task AddCommentAsync(TicketComment comment, CancellationToken cancellationToken = default)
    {
        if (comment.CommentId == 0)
        {
            comment.CommentId = _nextCommentId++;
        }

        Comments.Add(comment);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TicketComment>> GetCommentsAsync(long ticketId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TicketComment>>([.. Comments.Where(c => c.TicketId == ticketId).OrderBy(c => c.CreatedAt)]);
}

/// <summary>Records every queued notification, for assertions, instead of persisting anything.</summary>
public sealed class FakeNotificationService : INotificationService
{
    public List<(string TemplateCode, IReadOnlyDictionary<string, string> Variables, string RelatedEntityType, long? RelatedEntityId)> Calls { get; } = [];

    public Task<Notification> QueueAsync(
        string templateCode,
        IReadOnlyDictionary<string, string> variables,
        string relatedEntityType,
        long? relatedEntityId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((templateCode, variables, relatedEntityType, relatedEntityId));

        return Task.FromResult(new Notification
        {
            TemplateCode = templateCode,
            Recipients = templateCode,
            Subject = templateCode,
            BodyRendered = string.Empty,
            RelatedEntityType = relatedEntityType,
            RelatedEntityId = relatedEntityId,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}
