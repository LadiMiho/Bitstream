using System.Security.Cryptography;
using Bitstream.Application.Abstractions.Configuration;
using Bitstream.Application.Abstractions.Security;

namespace Bitstream.Application.Services.Identity;

/// <summary>
/// AES-256-GCM encryption of TOTP secrets, keyed by a master key resolved through
/// <see cref="ISecretResolver"/> (TR-SEC-28) — the same secret-store indirection every
/// integration credential in this codebase already uses, rather than a separate key-management
/// mechanism introduced just for this one column.
/// <para>
/// The master key is a base64-encoded 256-bit value under the secret name
/// <see cref="SecretName"/>, generated once per environment and never rotated in place: this
/// protector has no re-encryption path, so a key rotation is a data migration, not a
/// configuration change. That is an acceptable limitation for a scaffold-stage implementation
/// and is recorded in docs/open-items.md.
/// </para>
/// </summary>
public sealed class AesGcmTotpSecretProtector : ITotpSecretProtector
{
    /// <summary>Secret name the master key is stored under.</summary>
    public const string SecretName = "TotpEncryptionKey";

    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly ISecretResolver _secretResolver;

    public AesGcmTotpSecretProtector(ISecretResolver secretResolver) => _secretResolver = secretResolver;

    public async Task<byte[]> ProtectAsync(byte[] plainSecret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plainSecret);

        var key = await ResolveKeyAsync(cancellationToken).ConfigureAwait(false);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plainSecret.Length];
        var tag = new byte[TagSizeBytes];

        using (var aesGcm = new AesGcm(key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plainSecret, ciphertext, tag);
        }

        // Layout: nonce || tag || ciphertext. Fixed-size prefix, so unprotect needs no delimiter.
        return [.. nonce, .. tag, .. ciphertext];
    }

    public async Task<byte[]> UnprotectAsync(byte[] protectedSecret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);

        if (protectedSecret.Length <= NonceSizeBytes + TagSizeBytes)
        {
            throw new ArgumentException("Protected secret is too short to contain a nonce and tag.", nameof(protectedSecret));
        }

        var key = await ResolveKeyAsync(cancellationToken).ConfigureAwait(false);
        var nonce = protectedSecret.AsSpan(0, NonceSizeBytes);
        var tag = protectedSecret.AsSpan(NonceSizeBytes, TagSizeBytes);
        var ciphertext = protectedSecret.AsSpan(NonceSizeBytes + TagSizeBytes);
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private async Task<byte[]> ResolveKeyAsync(CancellationToken cancellationToken)
    {
        var encoded = await _secretResolver.GetRequiredSecretAsync(SecretName, cancellationToken).ConfigureAwait(false);

        byte[] key;

        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Secret '{SecretName}' is not valid base64. Generate a 256-bit key, e.g. " +
                "openssl rand -base64 32, and store it in the secret store.", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"Secret '{SecretName}' must decode to 32 bytes (AES-256); got {key.Length}.");
        }

        return key;
    }
}
