using Bitstream.Application.Abstractions.Configuration;

namespace Bitstream.Api.Configuration;

/// <summary>
/// Resolves secrets from the host's configuration providers, which on Windows Server means
/// environment variables set on the IIS application pool or the site, and User Secrets in
/// Development.
/// <para>
/// This is the composition root's answer to "where do secrets come from on this host"; the
/// adapters know only <see cref="ISecretResolver"/>. Swapping in a dedicated secret store —
/// Key Vault, CyberArk, a certificate-protected file — is a change to this one class.
/// </para>
/// <para>
/// It also enforces TR-SEC-28 rather than merely assuming it: a secret found in a JSON file
/// provider is refused, because that is a plain-text credential in a configuration file
/// regardless of how it got there.
/// </para>
/// </summary>
public sealed class ConfigurationSecretResolver : ISecretResolver
{
    /// <summary>Configuration section secrets are read from.</summary>
    public const string SectionName = "Secrets";

    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigurationSecretResolver> _logger;
    private readonly bool _refuseFileProviders;

    public ConfigurationSecretResolver(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<ConfigurationSecretResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);

        _configuration = configuration;
        _logger = logger;

        // Development keeps working with User Secrets, which is a file provider but not a
        // checked-in one. Everywhere else, a file-sourced secret is a deployment defect.
        _refuseFileProviders = !environment.IsDevelopment();
    }

    public ValueTask<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        cancellationToken.ThrowIfCancellationRequested();

        var key = $"{SectionName}:{secretName}";
        var value = _configuration[key];

        if (string.IsNullOrEmpty(value))
        {
            return ValueTask.FromResult<string?>(null);
        }

        if (_refuseFileProviders && IsFromJsonFile(key))
        {
            throw new InvalidOperationException(
                $"Secret '{secretName}' was supplied by a JSON configuration file. Credentials must come " +
                "from the secret store, never from a configuration file in plain text (TR-SEC-28).");
        }

        return ValueTask.FromResult<string?>(value);
    }

    public async ValueTask<string> GetRequiredSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        var value = await GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(value))
        {
            _logger.LogError("Required secret {SecretName} is not configured.", secretName);

            throw new InvalidOperationException(
                $"Required secret '{secretName}' is not configured. Set {SectionName}__{secretName} on the " +
                "application pool, or configure the secret store for this environment.");
        }

        return value;
    }

    private bool IsFromJsonFile(string key)
    {
        if (_configuration is not IConfigurationRoot root)
        {
            return false;
        }

        // The last provider that can supply the key is the one that wins.
        return root.Providers
            .Where(provider => provider.TryGet(key, out _))
            .Select(provider => provider.GetType().Name)
            .LastOrDefault()?
            .Contains("Json", StringComparison.OrdinalIgnoreCase) == true;
    }
}
