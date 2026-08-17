namespace Bitstream.Application.Abstractions.Security;

/// <summary>
/// Salted, adaptive one-way password hashing (TR-SEC-02). Reversible storage is prohibited by
/// the requirement, so there is deliberately no method on this interface that recovers a
/// plaintext password from a hash.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Algorithm tag stored alongside the hash, e.g. <c>Argon2id</c>, so hashes can be upgraded in place.</summary>
    string AlgorithmTag { get; }

    /// <summary>
    /// Hashes <paramref name="password"/> with a fresh random salt. The returned string embeds
    /// the salt and the cost parameters used, so a later change to the configured cost does not
    /// break verification of hashes created under the old cost.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against a previously stored <paramref name="hash"/>
    /// in constant time with respect to the comparison itself.
    /// </summary>
    bool Verify(string password, string hash);

    /// <summary>
    /// True when <paramref name="hash"/> was created under cost parameters weaker than the
    /// currently configured ones. The caller rehashes and replaces the stored value on the next
    /// successful login — the standard way to raise the cost floor without forcing a mass
    /// password reset.
    /// </summary>
    bool NeedsRehash(string hash);
}
