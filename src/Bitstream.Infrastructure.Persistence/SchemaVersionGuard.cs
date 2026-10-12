using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Checks at start-up that the deployed schema is the one this build was written against.
/// <para>
/// ADR-0002 puts the schema in numbered T-SQL scripts applied by the DBA, which means the
/// application and the database are deployed by two different steps and can therefore be
/// deployed out of order. Without this check, an application running against last release's
/// schema fails on the first request that touches a new column — far from the cause, and
/// usually in front of a user. TR-NFR-19 asks for deployment without data loss and a
/// documented rollback; refusing to start is what makes the wrong order recoverable.
/// </para>
/// <para>
/// Ordering on an upgrade is therefore: apply <c>db/mssql</c> first, then deploy the
/// application. The schema stays backward compatible for one version so that this never
/// requires downtime.
/// </para>
/// </summary>
public sealed class SchemaVersionGuard : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DatabaseOptions> _options;
    private readonly ILogger<SchemaVersionGuard> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public SchemaVersionGuard(
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseOptions> options,
        ILogger<SchemaVersionGuard> logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        // A missing connection string otherwise surfaces as SqlClient's bare "The ConnectionString
        // property has not been initialized" on the first database call — say which setting is
        // missing and how to supply it on the server instead.
        if (string.IsNullOrWhiteSpace(_configuration.GetConnectionString(options.ConnectionStringName)))
        {
            _logger.LogCritical(
                "No database connection string: ConnectionStrings:{ConnectionStringName} is empty for environment '{Environment}'. " +
                "Set it on the server, either as an environment variable in web.config " +
                "(<environmentVariable name=\"ConnectionStrings__{ConnectionStringName}\" value=\"Server=...;Database=...;...\" />) " +
                "or in appsettings.{Environment}.json next to the application dll. Every database call will fail until it is set.",
                options.ConnectionStringName, _environment.EnvironmentName, options.ConnectionStringName, _environment.EnvironmentName);

            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();

        int? deployed;

        try
        {
            deployed = await dbContext.GetDeployedSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unreachable database at start-up is not the same fault as a wrong schema, and
            // it must not be treated as one: the database may simply not be up yet, and the
            // readiness probe already reports it. Starting lets the host come up and report
            // unready rather than crash-looping under IIS.
            _logger.LogError(
                exception,
                "Could not read the deployed schema version at start-up. The readiness probe will report " +
                "database health; the application is starting without the schema check.");

            return;
        }

        if (deployed == BitstreamDbContext.ExpectedSchemaVersion)
        {
            _logger.LogInformation("Database schema version {SchemaVersion} matches this build.", deployed);
            return;
        }

        var message = deployed is null
            ? "The database has no applied schema version. Run db/Deploy-Database.ps1 before starting the application."
            : $"Database schema version is {deployed} but this build expects " +
              $"{BitstreamDbContext.ExpectedSchemaVersion}. Apply db/mssql before deploying the application.";

        if (options.FailFastOnSchemaMismatch)
        {
            _logger.LogCritical("{Message}", message);
            throw new InvalidOperationException(message);
        }

        _logger.LogError("{Message} FailFastOnSchemaMismatch is disabled, so the application is starting anyway.", message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
