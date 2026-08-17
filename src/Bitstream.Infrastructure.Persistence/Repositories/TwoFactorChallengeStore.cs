using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Repositories;

/// <summary>Implements <see cref="ITwoFactorChallengeStore"/> over <see cref="BitstreamDbContext"/>.</summary>
public sealed class TwoFactorChallengeStore : ITwoFactorChallengeStore
{
    private readonly BitstreamDbContext _dbContext;

    public TwoFactorChallengeStore(BitstreamDbContext dbContext) => _dbContext = dbContext;

    public async Task AddAsync(TwoFactorChallenge challenge, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        await _dbContext.TwoFactorChallenges.AddAsync(challenge, cancellationToken).ConfigureAwait(false);
    }

    public Task<TwoFactorChallenge?> FindByTokenAsync(string challengeToken, CancellationToken cancellationToken = default) =>
        _dbContext.TwoFactorChallenges
            .Include(challenge => challenge.User)
            .FirstOrDefaultAsync(challenge => challenge.ChallengeToken == challengeToken, cancellationToken);
}
