using Bitstream.Application.Abstractions.Integration;
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
public sealed class CrmApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"bitstream-crm-tests-{Guid.NewGuid()}";

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
            services.AddDbContext<BitstreamDbContext>(options => options.UseInMemoryDatabase(_databaseName));

            services.RemoveAll<ICrmGateway>();
            services.AddSingleton<ICrmGateway>(CrmGateway);
        });
    }

    /// <summary>Opens a scope for seeding data, asserting on state, or driving the dispatcher directly.</summary>
    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();
}
