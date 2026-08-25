using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Bitstream.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IActivationRequestRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class ActivationRequestRepository : IActivationRequestRepository
{
    private readonly BitstreamDbContext _dbContext;

    public ActivationRequestRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<ActivationRequest?> FindByIdAsync(long requestId, CancellationToken cancellationToken = default) =>
        _dbContext.ActivationRequests.FirstOrDefaultAsync(request => request.RequestId == requestId, cancellationToken);

    public Task<ActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        _dbContext.ActivationRequests.FirstOrDefaultAsync(request => request.PublicId == publicId, cancellationToken);

    public async Task AddAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _dbContext.ActivationRequests.AddAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<ActivationRequest> Items, int TotalCount)> SearchAsync(
        string? search, ActivationRequestStatus? status, long? ispId, int skip, int take, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.ActivationRequests.Include(request => request.Isp).AsQueryable();

        if (ispId is { } id)
        {
            query = query.Where(request => request.IspId == id);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = search.ToUpper();
            query = query.Where(request => request.PublicId.ToUpper().Contains(pattern) || request.PackageCode.ToUpper().Contains(pattern));
        }

        if (status is { } value)
        {
            query = query.Where(request => request.Status == value);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(request => request.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }
}
