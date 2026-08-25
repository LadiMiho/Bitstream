using Bitstream.Api.Tests.Activation;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Infrastructure.Persistence;
using Bitstream.Infrastructure.Persistence.Identity;
using Bitstream.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// Hosts the portal site (<c>Bitstream.Web</c>) against a database another factory already owns,
/// so one test can drive both halves of the split system.
/// <para>
/// The CRM round trip spans both hosts by design: a user submits an activation request on the
/// portal, and the API host is what dispatches it to CRM and applies CRM's events back. Pairing
/// this with <see cref="CrmApiFactory"/> on a shared InMemory database is the closest a test can
/// get to that topology — two pipelines, two service providers, one set of data — and it would
/// catch anything that only works because the two used to be a single process.
/// </para>
/// </summary>
public sealed class PortalApiFactory : WebApplicationFactory<WebHostEntryPoint>
{
    private readonly string _databaseName;
    private readonly FakeCrmGateway _crmGateway;

    /// <param name="databaseName">The <see cref="CrmApiFactory.DatabaseName"/> to share.</param>
    /// <param name="crmGateway">The same gateway double, so an assertion about calls sees both hosts' traffic.</param>
    public PortalApiFactory(string databaseName, FakeCrmGateway crmGateway)
    {
        _databaseName = databaseName;
        _crmGateway = crmGateway;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("CrmTests");

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:FailFastOnSchemaMismatch"] = "false",
                ["WorkingCalendar:TimeZoneId"] = "UTC",
                // Belt and braces: the portal host does not register the background jobs at all
                // (only the API host calls AddBitstreamBackgroundJobs), so this documents the
                // expectation rather than establishing it.
                ["Integration:OutboxDispatcher:Enabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveEntityFrameworkCoreServices();
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(_databaseName));
            // Same name as BitstreamDbContext, deliberately — see IdentityApiFactory's comment.
            services.AddDbContext<BitstreamIdentityDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<ICrmGateway>();
            services.AddSingleton<ICrmGateway>(_crmGateway);

            // As in CrmApiFactory: SqlPublicIdentifierGenerator needs a real DbConnection, which
            // the InMemory provider has none of.
            services.RemoveAll<IPublicIdentifierGenerator>();
            services.AddSingleton<IPublicIdentifierGenerator>(new FakePublicIdentifierGenerator());
        });
    }

    /// <summary>Opens a scope for seeding data or asserting on state through the portal host's provider.</summary>
    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();

    /// <summary>
    /// TR-SEC-26: <c>ConfigureApplicationCookie</c> sets <c>Cookie.SecurePolicy = Always</c>, so
    /// the auth cookie is silently dropped by the client on the default <c>http://localhost</c>
    /// base address <c>WebApplicationFactory</c> uses — see <c>IdentityApiFactory</c>'s copy of
    /// this same fix for the full explanation.
    /// </summary>
    public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

    public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.BaseAddress = new Uri("https://localhost");

        return base.CreateClient(options);
    }
}
