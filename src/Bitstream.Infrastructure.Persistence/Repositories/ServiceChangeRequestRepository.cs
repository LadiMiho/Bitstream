using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IServiceChangeRequestRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class ServiceChangeRequestRepository : IServiceChangeRequestRepository
{
    private readonly BitstreamDbContext _dbContext;

    public ServiceChangeRequestRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public Task<ServiceChangeRequest?> FindByIdAsync(long changeId, CancellationToken cancellationToken = default) =>
        _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(request => request.ChangeId == changeId, cancellationToken);

    public Task<ServiceChangeRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        _dbContext.ServiceChangeRequests.FirstOrDefaultAsync(request => request.PublicId == publicId, cancellationToken);

    public async Task AddAsync(ServiceChangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _dbContext.ServiceChangeRequests.AddAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
