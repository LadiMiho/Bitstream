using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence.HealthChecks;

/// <summary>
/// Reports database reachability, TR-ARC-05.
/// <para>
/// The probe is a round trip that the server actually executes, not just a pooled connection
/// handed back: a connection that opens but cannot run a statement is not a healthy database,
/// and readiness has to distinguish the two.
/// </para>
/// <para>
/// It also reports the deployed schema version, so that a mismatch is visible on the health
/// endpoint rather than only in a start-up failure (see ADR-0002).
/// </para>
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    /// <summary>Name this check is registered under.</summary>
    public const string Name = "database";

    private readonly BitstreamDbContext _dbContext;
    private readonly IOptions<DatabaseOptions> _options;

    public DatabaseHealthCheck(BitstreamDbContext dbContext, IOptions<DatabaseOptions> options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HealthCheckTimeout);

        var timestamp = Stopwatch.GetTimestamp();

        try
        {
            var schemaVersion = await _dbContext.GetDeployedSchemaVersionAsync(timeout.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(timestamp);

            var data = new Dictionary<string, object>
            {
                ["elapsedMs"] = Math.Round(elapsed.TotalMilliseconds, 1),
                ["schemaVersion"] = schemaVersion ?? -1,
                ["expectedSchemaVersion"] = BitstreamDbContext.ExpectedSchemaVersion
            };

            if (schemaVersion is null)
            {
                return HealthCheckResult.Unhealthy(
                    "Database reachable but ops.SchemaVersion is empty; the schema scripts have not been applied.",
                    data: data);
            }

            if (schemaVersion != BitstreamDbContext.ExpectedSchemaVersion)
            {
                return HealthCheckResult.Unhealthy(
                    $"Schema version mismatch: database is at {schemaVersion}, this build expects " +
                    $"{BitstreamDbContext.ExpectedSchemaVersion}. Apply db/mssql before deploying.",
                    data: data);
            }

            return HealthCheckResult.Healthy("Database reachable.", data);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"Database did not respond within {options.HealthCheckTimeout.TotalSeconds:F0} s.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message can name the server and the database, which is exactly what an
            // operator needs and exactly what should not reach an anonymous caller. The
            // response writer decides how much of this is exposed.
            return HealthCheckResult.Unhealthy("Database unreachable.", exception);
        }
    }
}
