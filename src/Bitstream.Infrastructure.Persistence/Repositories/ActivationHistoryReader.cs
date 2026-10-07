using System.Globalization;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IActivationHistoryReader"/> over <see cref="BitstreamDbContext"/>; read-only, untracked.</summary>
public sealed class ActivationHistoryReader : IActivationHistoryReader
{
    private const string EntityType = "ActivationRequest";

    private readonly BitstreamDbContext _dbContext;

    public ActivationHistoryReader(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<ActivationHistory> GetAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // CRM may address an inbound event by its own ticket number instead of the portal ID,
        // and that is what the stored message's RelatedPublicId then holds.
        var publicId = request.PublicId;
        var crmTicketId = request.CrmTicketId;

        var messages = await _dbContext.IntegrationMessages
            .AsNoTracking()
            .Where(m => m.RelatedPublicId == publicId || (crmTicketId != null && m.RelatedPublicId == crmTicketId))
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Only what a signed-in user did: CRM-driven and system changes have no actor and are
        // already on the timeline as the message that caused them.
        var entityId = request.RequestId.ToString(CultureInfo.InvariantCulture);
        var audit = await _dbContext.AuditLog
            .AsNoTracking()
            .Where(a => a.EntityType == EntityType && a.EntityId == entityId && a.ActorUserId != null && a.ActionCode.StartsWith("ActivationRequest."))
            .OrderBy(a => a.Timestamp)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var actorIds = audit.Select(a => a.ActorUserId!.Value).Distinct().ToList();
        var actorNames = actorIds.Count == 0
            ? new Dictionary<long, string>()
            : await _dbContext.Users
                .AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken)
                .ConfigureAwait(false);

        return new ActivationHistory(messages, audit, actorNames);
    }
}
