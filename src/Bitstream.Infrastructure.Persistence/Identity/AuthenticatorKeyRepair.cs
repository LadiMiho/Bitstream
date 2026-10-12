using Bitstream.Application.Abstractions.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence.Identity;

/// <summary>
/// Implements <see cref="IAuthenticatorKeyRepair"/>. Reads only the token keys — never the
/// encrypted <c>Value</c>, whose decryption is what fails — and deletes by key, so it works
/// on exactly the rows Identity itself can no longer load.
/// </summary>
public sealed class AuthenticatorKeyRepair : IAuthenticatorKeyRepair
{
    /// <summary>The login provider ASP.NET Core Identity's own store uses for its internal tokens.</summary>
    private const string IdentityStoreProvider = "[AspNetUserStore]";

    private static readonly string[] TokenNames = ["AuthenticatorKey", "RecoveryCodes"];

    private readonly BitstreamIdentityDbContext _dbContext;

    public AuthenticatorKeyRepair(BitstreamIdentityDbContext dbContext) => _dbContext = dbContext;

    public async Task<int> ClearAsync(long userId, CancellationToken cancellationToken = default)
    {
        var keys = await _dbContext.UserTokens
            .Where(t => t.UserId == userId && t.LoginProvider == IdentityStoreProvider && TokenNames.Contains(t.Name))
            .Select(t => new { t.UserId, t.LoginProvider, t.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var key in keys)
        {
            var tracked = _dbContext.UserTokens.Local.FirstOrDefault(t =>
                t.UserId == key.UserId && t.LoginProvider == key.LoginProvider && t.Name == key.Name);

            _dbContext.UserTokens.Remove(tracked ?? new IdentityUserToken<long>
            {
                UserId = key.UserId,
                LoginProvider = key.LoginProvider,
                Name = key.Name
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return keys.Count;
    }
}
