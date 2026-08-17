using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Registration entry point for the persistence layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the EF Core context against the connection string named
    /// <c>BitstreamDb</c>. The connection string is supplied per environment and its
    /// credential comes from the secret store, never from a checked-in file (TR-SEC-28).
    /// </summary>
    public static IServiceCollection AddBitstreamPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString("BitstreamDb");

        services.AddDbContext<BitstreamDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                // TR-NFR-07: transient faults are retried rather than surfaced to the user.
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                sql.CommandTimeout(30);
            }));

        return services;
    }
}
