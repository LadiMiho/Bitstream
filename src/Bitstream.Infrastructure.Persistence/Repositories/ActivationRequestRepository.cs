using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
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
}
