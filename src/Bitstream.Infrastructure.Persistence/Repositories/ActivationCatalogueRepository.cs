using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IActivationCatalogueRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class ActivationCatalogueRepository : IActivationCatalogueRepository
{
    private readonly BitstreamDbContext _dbContext;

    public ActivationCatalogueRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<Package>> GetPackagesAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.Packages.OrderBy(package => package.Tier).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ActivationClassification>> GetClassificationsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.ActivationClassifications.OrderBy(classification => classification.Name).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ContractDuration>> GetContractDurationsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.ContractDurations.OrderBy(duration => duration.Months).ToListAsync(cancellationToken).ConfigureAwait(false);
}
