using Bitstream.Api.Tests.Activation;
using Bitstream.Api.Tests.Identity;
using Bitstream.Api.Tests.Integration;
using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Services.Integration;
using Bitstream.Domain.Enums;
using Bitstream.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bitstream.Api.Tests.Logging;

/// <summary>The outbox dispatcher logs each failed attempt with its number and the error, so a request stuck in Pending CRM Sync can be explained from the log.</summary>
public sealed class OutboxDispatcherLoggingTests
{
    [Fact]
    public async Task A_failed_attempt_is_logged_with_its_number_and_the_error()
    {
        var captured = new CapturingLoggerProvider();
        await using var baseFactory = new CrmApiFactory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(captured)));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BitstreamDbContext>();
            var isp = await IdentitySeeder.AddIspAsync(db, "Log ISP", "L00000960", "LOG");
            await ActivationSeeder.AddRequestAsync(db, isp.IspId, "LOG_001", ActivationRequestStatus.PendingCrmSync);

            var command = new CreateCrmCustomerCommand(
                new IntegrationEnvelope(Guid.NewGuid(), "corr", "LOG_001", DateTimeOffset.UtcNow),
                "LOG_001", "Log ISP", "L00000960", "Contact", "log@example.com", "+355691234567", "41.3275,19.8187");

            await scope.ServiceProvider.GetRequiredService<IIntegrationOutbox>().EnqueueOutboundAsync(
                TargetSystem.Crm, "INT-CRM-01", "CREATE_CUSTOMER", "LOG_001", System.Text.Json.JsonSerializer.Serialize(command), "corr", "LOG_001");
        }

        baseFactory.CrmGateway.NextCreateCustomerResult =
            IntegrationResult<CreateCrmCustomerResult>.TechnicalFailure("CRM endpoint unreachable");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync();
        }

        Assert.Contains(captured.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("INT-CRM-01 CREATE_CUSTOMER for LOG_001 failed (attempt 1 of 5); will retry: CRM endpoint unreachable", StringComparison.Ordinal));
    }
}
