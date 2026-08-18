using Bitstream.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Bitstream.Api.Tests;

/// <summary>
/// Hosts the real application pipeline for endpoint tests.
/// <para>
/// Nothing about the pipeline is replaced: the point of these tests is that the middleware
/// order, the health check registrations and the response writer behave as configured, and a
/// re-declared test pipeline would not prove that.
/// </para>
/// <para>
/// Configuration is overridden only where the host machine would otherwise decide the outcome:
/// a syntactically valid connection string that points at nothing, short probe timeouts so the
/// suite does not wait on TCP, and a time zone every host has.
/// </para>
/// </summary>
public sealed class BitstreamApiFactory : WebApplicationFactory<WebHostEntryPoint>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Valid enough for EF to build a context; deliberately unreachable, because
                // "database down" is the state these tests assert on.
                ["ConnectionStrings:BitstreamDb"] =
                    "Server=localhost,14330;Database=BitstreamPortalTests;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True;Connect Timeout=1",

                ["Database:HealthCheckTimeout"] = "00:00:01",
                ["Database:MaxRetryCount"] = "0",

                // Schema mismatch must not stop the host here: the guard's own behaviour is
                // that an unreachable database is not a mismatch, and these tests exercise the
                // endpoints, not the guard.
                ["Database:FailFastOnSchemaMismatch"] = "false",

                ["Integration:Crm:HealthCheckTimeout"] = "00:00:01",
                ["Integration:Bi:HealthCheckTimeout"] = "00:00:01",
                ["Integration:Sap:HealthCheckTimeout"] = "00:00:01",
                ["Integration:Smtp:HealthCheckTimeout"] = "00:00:01",

                // Windows time zone IDs resolve on Linux through ICU, but not on a host with no
                // tz data at all. The calendar is not what is under test.
                ["WorkingCalendar:TimeZoneId"] = "UTC"
            });
        });
    }
}
