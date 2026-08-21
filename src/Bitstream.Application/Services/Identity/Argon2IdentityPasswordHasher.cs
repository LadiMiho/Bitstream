using Bitstream.Application.Abstractions.Security;
using Bitstream.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// Adapts the existing <see cref="IPasswordHasher"/> (Argon2id, TR-SEC-02) to
/// <c>UserManager&lt;User&gt;</c>'s own hasher contract, so <c>CreateAsync</c>/
/// <c>CheckPasswordAsync</c> use exactly the same algorithm this app already used before
/// adopting <c>UserManager</c> — not Identity's PBKDF2 default.
/// </summary>
public sealed class Argon2IdentityPasswordHasher : Microsoft.AspNetCore.Identity.IPasswordHasher<User>
{
    private readonly IPasswordHasher _passwordHasher;

    public Argon2IdentityPasswordHasher(IPasswordHasher passwordHasher) => _passwordHasher = passwordHasher;

    public string HashPassword(User user, string password) => _passwordHasher.Hash(password);

    public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
    {
        if (!_passwordHasher.Verify(providedPassword, hashedPassword))
        {
            return PasswordVerificationResult.Failed;
        }

        return _passwordHasher.NeedsRehash(hashedPassword)
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }
}
