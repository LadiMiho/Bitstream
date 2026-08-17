using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Database connection behaviour. Externalised so that timeouts and retry counts can be tuned
/// per environment without a code deployment (TR-ARC-06).
/// <para>
/// The connection string itself is not held here: it comes from the configured connection
/// string name, supplied per environment from the secret store or from Integrated Security
/// with the application pool identity (TR-SEC-28).
/// </para>
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Name of the connection string entry to use.</summary>
    public string ConnectionStringName { get; set; } = "BitstreamDb";

    /// <summary>Command timeout in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Transient-fault retry count (TR-NFR-07).</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Upper bound on the delay between transient-fault retries.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout applied to the health check probe, kept short so readiness stays responsive.</summary>
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a schema version mismatch prevents the application from starting.
    /// True everywhere it matters: running against a schema the build was not written for
    /// produces failures far from their cause (see ADR-0002).
    /// </summary>
    public bool FailFastOnSchemaMismatch { get; set; } = true;
}

/// <summary>Validates the database options.</summary>
public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            failures.Add("Database:ConnectionStringName must be set.");
        }

        if (options.CommandTimeoutSeconds < 1)
        {
            failures.Add("Database:CommandTimeoutSeconds must be at least 1.");
        }

        if (options.MaxRetryCount < 0)
        {
            failures.Add("Database:MaxRetryCount must not be negative.");
        }

        if (options.HealthCheckTimeout <= TimeSpan.Zero)
        {
            failures.Add("Database:HealthCheckTimeout must be greater than zero (TR-ARC-05).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
