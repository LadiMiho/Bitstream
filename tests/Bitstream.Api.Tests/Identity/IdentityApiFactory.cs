using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bitstream.Api.Tests.Identity;

/// <summary>
/// Hosts the real pipeline — real authentication handler, real authorization handler, real
/// endpoints — for the TR-SEC-19, lockout and session-expiry tests, with EF Core's InMemory
/// provider standing in for SQL Server (no SQL Server instance is available in this
/// environment; see README.md "Verification status"). Bulk operations
/// (<c>ExecuteUpdateAsync</c>, used by <c>UserSessionStore</c>'s cascade revoke) are not
/// supported by that provider, so tests exercising those go through
/// <c>AdministrationServiceTests</c> against hand-written fakes instead.
/// <para>
/// One factory per test: <c>_databaseName</c> is unique per instance, so tests that want
/// isolation create their own <c>new IdentityApiFactory()</c> rather than sharing one via
/// <c>IClassFixture</c>.
/// </para>
/// </summary>
public sealed class IdentityApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"bitstream-identity-tests-{Guid.NewGuid()}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("IdentityTests");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // SchemaVersionGuard's health-check path is never reached under InMemory (its
                // GetDbConnection() call throws, which the guard already treats as "database not
                // reachable yet" rather than a hard failure) — this just documents the intent.
                ["Database:FailFastOnSchemaMismatch"] = "false",
                ["WorkingCalendar:TimeZoneId"] = "UTC",
                // Tests that want OutboxDispatcher drive it explicitly (DispatchBatchAsync) for
                // determinism; the timer-driven background loop would otherwise run unsupervised
                // against every test's InMemory database, including tests that never touch it.
                ["Integration:OutboxDispatcher:Enabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BitstreamDbContext>>();
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    /// <summary>Opens a scope for seeding data before a test issues requests, or for asserting on state afterwards.</summary>
    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();
}
