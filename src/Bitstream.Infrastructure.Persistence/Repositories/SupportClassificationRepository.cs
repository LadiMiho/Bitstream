using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="ISupportClassificationRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class SupportClassificationRepository : ISupportClassificationRepository
{
    private readonly BitstreamDbContext _dbContext;

    public SupportClassificationRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<SupportClassification>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.SupportClassifications
            .OrderBy(c => c.Level)
            .ThenBy(c => c.DisplayOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<SupportClassification?> FindByIdAsync(long classificationId, CancellationToken cancellationToken = default) =>
        _dbContext.SupportClassifications.FirstOrDefaultAsync(c => c.ClassificationId == classificationId, cancellationToken);

    public Task<SupportClassification?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        _dbContext.SupportClassifications.FirstOrDefaultAsync(c => c.Code == code, cancellationToken);

    public async Task AddAsync(SupportClassification classification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(classification);

        await _dbContext.SupportClassifications.AddAsync(classification, cancellationToken).ConfigureAwait(false);
    }
}
