using Bitstream.Infrastructure.Persistence;
using Bitstream.Infrastructure.Persistence.Identity;
using Bitstream.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
public sealed class IdentityApiFactory : WebApplicationFactory<WebHostEntryPoint>
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
            services.RemoveEntityFrameworkCoreServices();
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            // Same database name, deliberately: BitstreamDbContext and BitstreamIdentityDbContext
            // both map User/Role (BitstreamDbContext's own doc comment explains why) — EF Core's
            // InMemory provider shares its store by name regardless of DbContext type, so a user
            // IdentitySeeder writes through BitstreamDbContext is exactly the row UserManager
            // (wired to BitstreamIdentityDbContext) reads back.
            services.AddDbContext<BitstreamIdentityDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    /// <summary>Opens a scope for seeding data before a test issues requests, or for asserting on state afterwards.</summary>
    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();

    /// <summary>
    /// TR-SEC-26: <c>ConfigureApplicationCookie</c> sets <c>Cookie.SecurePolicy = Always</c>, so
    /// the auth cookie is silently dropped by the client on the default <c>http://localhost</c>
    /// base address <c>WebApplicationFactory</c> uses — login/verify would set it, but no later
    /// request would ever send it back. <c>TestServer</c> honours the URI scheme without needing
    /// a real certificate, so an https base address alone is enough to make it behave correctly.
    /// </summary>
    public override HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.BaseAddress = new Uri("https://localhost");

        return base.CreateClient(options);
    }
}
