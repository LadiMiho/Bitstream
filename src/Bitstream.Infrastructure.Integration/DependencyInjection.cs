using Bitstream.Application.Abstractions.Integration;
using Bitstream.Infrastructure.Integration.Bi;
using Bitstream.Infrastructure.Integration.Crm;
using Bitstream.Infrastructure.Integration.Mail;
using Bitstream.Infrastructure.Integration.Sap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bitstream.Infrastructure.Integration;

/// <summary>
/// Registration entry point for the integration layer (TRD 2.2 "Integration Layer").
/// Every adapter is bound to a port here; no other project may construct an adapter.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddBitstreamIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CrmOptions>(configuration.GetSection(CrmOptions.SectionName));
        services.Configure<BiOptions>(configuration.GetSection(BiOptions.SectionName));
        services.Configure<SapOptions>(configuration.GetSection(SapOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));

        // Named clients so that timeouts, certificates and resilience handlers are configured
        // per target system rather than globally (TR-INT-08).
        services.AddHttpClient<ICrmGateway, CrmHttpGateway>();
        services.AddHttpClient<IBiGateway, BiGateway>();
        services.AddHttpClient<ISapGateway, SapGateway>();

        services.AddSingleton<IEmailGateway, SmtpEmailGateway>();

        return services;
    }
}
