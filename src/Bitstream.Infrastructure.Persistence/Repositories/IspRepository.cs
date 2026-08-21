using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IIspRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class IspRepository : IIspRepository
{
    private readonly BitstreamDbContext _dbContext;

    public IspRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<Isp?> FindByIdAsync(long ispId, CancellationToken cancellationToken = default) =>
        _dbContext.Isps.FirstOrDefaultAsync(isp => isp.IspId == ispId, cancellationToken);

    public Task<bool> NiptExistsAsync(string nipt, CancellationToken cancellationToken = default) =>
        _dbContext.Isps.AnyAsync(isp => isp.Nipt == nipt, cancellationToken);

    public Task<Isp?> FindByCrmBpReferenceAsync(string crmBpReference, CancellationToken cancellationToken = default) =>
        _dbContext.Isps.FirstOrDefaultAsync(isp => isp.CrmBpReference == crmBpReference, cancellationToken);

    public async Task AddAsync(Isp isp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isp);

        await _dbContext.Isps.AddAsync(isp, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<Isp> Items, int TotalCount)> SearchAsync(
        string? search, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Isps.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = search.ToUpper();
            query = query.Where(isp => isp.Name.ToUpper().Contains(pattern) || isp.Nipt.ToUpper().Contains(pattern));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(isp => isp.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }
}
