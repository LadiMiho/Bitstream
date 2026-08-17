namespace Bitstream.Application.Abstractions.Security;

/// <summary>
/// Encrypts a TOTP secret at rest. <see cref="Domain.Entities.User.TotpSecret"/> is described as
/// an "encrypted TOTP seed"; this is what makes that true, rather than the column merely being
/// typed <c>varbinary</c> while holding the seed in the clear.
/// </summary>
public interface ITotpSecretProtector
{
    Task<byte[]> ProtectAsync(byte[] plainSecret, CancellationToken cancellationToken = default);

    Task<byte[]> UnprotectAsync(byte[] protectedSecret, CancellationToken cancellationToken = default);
}
