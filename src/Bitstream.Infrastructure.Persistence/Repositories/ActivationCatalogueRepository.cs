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

    public async Task<IReadOnlyList<PackageOffer>> GetPackageOffersAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.PackageOffers
            .OrderBy(offer => offer.PackageCode)
            .ThenBy(offer => offer.ContractDurationMonths)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<PackageOffer?> FindPackageOfferAsync(string packageCode, int contractDurationMonths, CancellationToken cancellationToken = default) =>
        _dbContext.PackageOffers.FirstOrDefaultAsync(
            offer => offer.PackageCode == packageCode && offer.ContractDurationMonths == contractDurationMonths, cancellationToken);

    public Task<PackageOffer?> FindPackageOfferByCodeAsync(string offerCode, CancellationToken cancellationToken = default) =>
        _dbContext.PackageOffers.FirstOrDefaultAsync(offer => offer.OfferCode == offerCode, cancellationToken);

    public async Task AddPackageOfferAsync(PackageOffer offer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);

        await _dbContext.PackageOffers.AddAsync(offer, cancellationToken).ConfigureAwait(false);
    }

    public Task<Package?> FindPackageAsync(string code, CancellationToken cancellationToken = default) =>
        _dbContext.Packages.FirstOrDefaultAsync(package => package.Code == code, cancellationToken);

    public async Task AddPackageAsync(Package package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        await _dbContext.Packages.AddAsync(package, cancellationToken).ConfigureAwait(false);
    }

    public Task<ContractDuration?> FindContractDurationAsync(int months, CancellationToken cancellationToken = default) =>
        _dbContext.ContractDurations.FirstOrDefaultAsync(duration => duration.Months == months, cancellationToken);

    public async Task AddContractDurationAsync(ContractDuration duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(duration);

        await _dbContext.ContractDurations.AddAsync(duration, cancellationToken).ConfigureAwait(false);
    }
}
