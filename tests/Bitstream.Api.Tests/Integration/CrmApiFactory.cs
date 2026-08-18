using Bitstream.Api.Tests.Activation;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bitstream.Api.Tests.Integration;

/// <summary>
/// Hosts the real pipeline for the CRM integration tests (TRD 7.3): real endpoints, real
/// <c>ActivationRequestService</c>, real <c>OutboxDispatcher</c> and <c>InboundEventService</c>,
/// with EF Core's InMemory provider standing in for SQL Server (see
/// <c>Identity/IdentityApiFactory</c> for why) and <see cref="FakeCrmGateway"/> standing in for
/// CRM itself (TRD 11.4 open item 1 — there is no real contract to call).
/// <para>
/// <c>OutboxDispatcher</c>'s background poll loop is disabled; tests resolve it from
/// <see cref="CreateAsyncScope"/>'s parent and call <c>DispatchBatchAsync</c> directly so a
/// dispatch happens exactly when the test says it does, not on a timer race.
/// </para>
/// </summary>
public sealed class CrmApiFactory : WebApplicationFactory<ApiHostEntryPoint>
{
    /// <summary>
    /// Shared with <see cref="PortalApiFactory"/> so the two hosts see one database, which is
    /// what the split actually looks like in deployment: a request submitted on the portal is
    /// dispatched to CRM, and CRM's events applied, by the API host — same data, two processes.
    /// </summary>
    public string DatabaseName { get; } = $"bitstream-crm-tests-{Guid.NewGuid()}";

    public FakeCrmGateway CrmGateway { get; } = new();

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
                ["Integration:OutboxDispatcher:Enabled"] = "false"
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveEntityFrameworkCoreServices();
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(DatabaseName));

            services.RemoveAll<ICrmGateway>();
            services.AddSingleton<ICrmGateway>(CrmGateway);

            // SqlPublicIdentifierGenerator calls ops.usp_NextPublicIdentifier over the context's
            // DbConnection, and the InMemory provider has no connection to give — GetDbConnection()
            // throws, so every submit through the API would 500. These tests are about the CRM
            // round trip, so a counter is the right stand-in. Note this means the gap-free series
            // itself (TR-DAT-02b) is still unproven by any automated test: it lives in the stored
            // procedure and needs a real SQL Server, which this environment does not have (see
            // README.md "Verification status").
            services.RemoveAll<IPublicIdentifierGenerator>();
            services.AddSingleton<IPublicIdentifierGenerator>(new FakePublicIdentifierGenerator());
        });
    }

    /// <summary>Opens a scope for seeding data, asserting on state, or driving the dispatcher directly.</summary>
    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();
}
