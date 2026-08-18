using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IActiveLineRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class ActiveLineRepository : IActiveLineRepository
{
    private readonly BitstreamDbContext _dbContext;

    public ActiveLineRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<ActiveLine?> FindByIdAsync(long lineId, CancellationToken cancellationToken = default) =>
        _dbContext.ActiveLines.FirstOrDefaultAsync(line => line.LineId == lineId, cancellationToken);

    public Task<ActiveLine?> FindByIspAndContractAsync(long ispId, string contractId, CancellationToken cancellationToken = default) =>
        _dbContext.ActiveLines.FirstOrDefaultAsync(line => line.IspId == ispId && line.ContractId == contractId, cancellationToken);

    public async Task AddAsync(ActiveLine line, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(line);

        await _dbContext.ActiveLines.AddAsync(line, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        _dbContext.ActiveLines.CountAsync(cancellationToken);
}
