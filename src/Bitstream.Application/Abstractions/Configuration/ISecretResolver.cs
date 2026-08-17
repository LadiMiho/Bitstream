namespace Bitstream.Application.Abstractions.Configuration;

/// <summary>
/// Resolves a named secret from the platform secret store.
/// <para>
/// TR-SEC-28: credentials, tokens and integration secrets are held in a secret store and never
/// in source code or in configuration files in plain text. Options classes therefore carry the
/// <em>name</em> of a secret — <c>CredentialSecretName</c> — and never its value; the adapter
/// asks this resolver for the value at the point of use.
/// </para>
/// <para>
/// The indirection also satisfies TR-INT-06's rotation requirement: rotating a credential is a
/// change in the store, not a code release, because nothing caches the value.
/// </para>
/// </summary>
public interface ISecretResolver
{
    /// <summary>Returns the secret, or null when it is not configured.</summary>
    /// <param name="secretName">Name of the secret, as held in an options class.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the secret, or throws when it is missing. Used where proceeding without the
    /// credential would produce a confusing downstream failure instead of a clear one.
    /// </summary>
    /// <param name="secretName">Name of the secret.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string> GetRequiredSecretAsync(string secretName, CancellationToken cancellationToken = default);
}
