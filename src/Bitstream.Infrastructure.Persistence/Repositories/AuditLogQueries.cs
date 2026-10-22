using Bitstream.Application.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IAuditLogQueries"/> over <see cref="BitstreamDbContext"/>, read-only (no tracking).</summary>
public sealed class AuditLogQueries : IAuditLogQueries
{
    private readonly BitstreamDbContext _dbContext;

    public AuditLogQueries(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<(IReadOnlyList<AuditLogRow> Items, int TotalCount)> SearchAsync(
        AuditLogSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _dbContext.AuditLog.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = filter.Search.Trim().ToUpper();
            query = query.Where(a =>
                a.ActionCode.ToUpper().Contains(pattern)
                || (a.EntityId != null && a.EntityId.ToUpper().Contains(pattern))
                || a.CorrelationId.ToUpper().Contains(pattern));
        }

        if (!string.IsNullOrWhiteSpace(filter.ActionCode))
        {
            query = query.Where(a => a.ActionCode == filter.ActionCode);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            query = query.Where(a => a.EntityType == filter.EntityType);
        }

        if (filter.SystemOnly)
        {
            query = query.Where(a => a.ActorUserId == null);
        }
        else if (filter.ActorUserId is { } actorId)
        {
            query = query.Where(a => a.ActorUserId == actorId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(a => a.Timestamp >= from);
        }

        if (filter.Before is { } before)
        {
            query = query.Where(a => a.Timestamp < before);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var entries = await query
            .OrderByDescending(a => a.Timestamp)
            .ThenByDescending(a => a.AuditId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var actors = await ActorsAsync(entries.Where(e => e.ActorUserId is not null).Select(e => e.ActorUserId!.Value), cancellationToken).ConfigureAwait(false);

        return ([.. entries.Select(e => Row(e, actors))], totalCount);
    }

    public async Task<AuditLogRow?> FindAsync(long auditId, CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.AuditLog.AsNoTracking().FirstOrDefaultAsync(a => a.AuditId == auditId, cancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return null;
        }

        var actors = await ActorsAsync(entry.ActorUserId is { } id ? [id] : [], cancellationToken).ConfigureAwait(false);
        return Row(entry, actors);
    }

    public async Task<(IReadOnlyList<string> ActionCodes, IReadOnlyList<string> EntityTypes, IReadOnlyList<AuditActor> Actors)> GetFilterOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var actionCodes = await _dbContext.AuditLog.AsNoTracking().Select(a => a.ActionCode).Distinct().OrderBy(c => c)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var entityTypes = await _dbContext.AuditLog.AsNoTracking().Select(a => a.EntityType).Distinct().OrderBy(t => t)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var actorIds = await _dbContext.AuditLog.AsNoTracking().Where(a => a.ActorUserId != null).Select(a => a.ActorUserId!.Value).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var actors = await ActorsAsync(actorIds, cancellationToken).ConfigureAwait(false);

        return (actionCodes, entityTypes,
            [.. actors.Select(pair => new AuditActor(pair.Key, pair.Value.Name, pair.Value.Email)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)]);
    }

    private async Task<Dictionary<long, (string Name, string Email)>> ActorsAsync(IEnumerable<long> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        return await _dbContext.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => (u.FullName, u.Email ?? string.Empty), cancellationToken)
            .ConfigureAwait(false);
    }

    private static AuditLogRow Row(Domain.Entities.AuditLog entry, Dictionary<long, (string Name, string Email)> actors) =>
        entry.ActorUserId is { } id && actors.TryGetValue(id, out var actor)
            ? new AuditLogRow(entry, actor.Name, actor.Email)
            : new AuditLogRow(entry, null, null);
}
