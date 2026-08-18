using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="ISyncStateStore"/> over <see cref="BitstreamDbContext"/>. Self-saving, like <c>AuditWriter</c> — a sync job's own bookkeeping is not part of any caller's business transaction.</summary>
public sealed class SyncStateStore : ISyncStateStore
{
    private readonly BitstreamDbContext _dbContext;

    public SyncStateStore(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<SyncState> GetOrCreateAsync(string syncKey, CancellationToken cancellationToken = default)
    {
        var state = await _dbContext.SyncStates.FirstOrDefaultAsync(s => s.SyncKey == syncKey, cancellationToken).ConfigureAwait(false);

        if (state is not null)
        {
            return state;
        }

        state = new SyncState { SyncKey = syncKey };
        await _dbContext.SyncStates.AddAsync(state, cancellationToken).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return state;
    }

    public async Task SaveAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
