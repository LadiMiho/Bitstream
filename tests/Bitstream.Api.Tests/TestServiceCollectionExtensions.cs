using Bitstream.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bitstream.Api.Tests;

/// <summary>
/// Helpers for the <c>WebApplicationFactory</c>-based tests, which host the real pipeline and
/// then swap SQL Server for EF Core's InMemory provider.
/// </summary>
internal static class TestServiceCollectionExtensions
{
    /// <summary>
    /// Removes every Entity Framework Core registration the application made, so a test can add
    /// the InMemory provider in its place.
    /// <para>
    /// Removing <c>DbContextOptions&lt;BitstreamDbContext&gt;</c> alone is not enough:
    /// <c>UseSqlServer</c> also registers the SQL Server provider's own services in the same
    /// container, and EF refuses to resolve a context when two providers' services are both
    /// present ("Only a single database provider can be registered in a service provider").
    /// Everything in the <c>Microsoft.EntityFrameworkCore</c> namespace therefore has to go
    /// before the InMemory provider is registered.
    /// </para>
    /// </summary>
    public static IServiceCollection RemoveEntityFrameworkCoreServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptors = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(BitstreamDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType == typeof(DbContextOptions<BitstreamDbContext>)
                || (descriptor.ServiceType.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false))
            .ToList();

        foreach (var descriptor in descriptors)
        {
            services.Remove(descriptor);
        }

        return services;
    }
}
