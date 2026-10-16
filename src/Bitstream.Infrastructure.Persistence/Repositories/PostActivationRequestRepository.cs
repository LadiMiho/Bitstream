using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="IPostActivationRequestRepository"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class PostActivationRequestRepository : IPostActivationRequestRepository
{
    private readonly BitstreamDbContext _dbContext;

    public PostActivationRequestRepository(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<string>> GetPhoneNumbersAsync(string? ispTicketCode, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.BiPhoneNumbers.AsNoTracking().Select(p => p.Phonenumber);

        if (ispTicketCode is not null)
        {
            // Upper-cased on both sides so the match ignores case whatever the column collation
            // (and under the InMemory provider); PrefixOf re-checks the exact prefix below.
            var prefix = ispTicketCode.ToUpperInvariant() + BiPhoneNumber.PrefixSeparator;
            query = query.Where(p => p.ToUpper().StartsWith(prefix));
        }

        var numbers = await query.OrderBy(p => p).ToListAsync(cancellationToken).ConfigureAwait(false);

        return ispTicketCode is null
            ? numbers
            : [.. numbers.Where(p => string.Equals(BiPhoneNumber.PrefixOf(p), ispTicketCode, StringComparison.OrdinalIgnoreCase))];
    }

    public Task<bool> PhoneNumberExistsAsync(string phonenumber, CancellationToken cancellationToken = default) =>
        _dbContext.BiPhoneNumbers.AnyAsync(p => p.Phonenumber == phonenumber, cancellationToken);

    public Task<PostActivationRequest?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken = default) =>
        _dbContext.PostActivationRequests
            .Include(r => r.Isp)
            .FirstOrDefaultAsync(r => r.PublicId == publicId, cancellationToken);

    public async Task<(IReadOnlyList<PostActivationRequest> Items, int TotalCount)> SearchAsync(
        PostActivationRequestSearchFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _dbContext.PostActivationRequests.AsNoTracking().Include(r => r.Isp).AsQueryable();

        if (filter.IspId is { } id)
        {
            query = query.Where(r => r.IspId == id);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var pattern = filter.Search.Trim().ToUpper();
            query = query.Where(r => r.PublicId.ToUpper().Contains(pattern) || r.Phonenumber.ToUpper().Contains(pattern));
        }

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(r => r.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Classification1Code))
        {
            query = query.Where(r => r.Classification1Code == filter.Classification1Code);
        }

        if (!string.IsNullOrWhiteSpace(filter.Classification2Code))
        {
            query = query.Where(r => r.Classification2Code == filter.Classification2Code);
        }

        if (filter.SubmittedFrom is { } from)
        {
            query = query.Where(r => r.CreatedAt >= from);
        }

        if (filter.SubmittedBefore is { } before)
        {
            query = query.Where(r => r.CreatedAt < before);
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.RequestId)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    public async Task<IReadOnlyList<IntegrationMessage>> GetMessagesAsync(string publicId, CancellationToken cancellationToken = default) =>
        await _dbContext.IntegrationMessages
            .AsNoTracking()
            .Where(m => m.RelatedPublicId == publicId)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.MessageId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task AddAsync(PostActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _dbContext.PostActivationRequests.AddAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
