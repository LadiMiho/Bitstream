using Bitstream.Application.Abstractions.Integration;
using Bitstream.Infrastructure.Integration.Bi;
using Bitstream.Infrastructure.Integration.Crm;
using Bitstream.Infrastructure.Integration.Http;
using Bitstream.Infrastructure.Integration.Mail;
using Bitstream.Infrastructure.Integration.Sap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        // TR-ARC-06: endpoints, timeouts, retry budgets, distribution groups and the redirect
        // mailbox are all configuration. Nothing here is a literal in an adapter.
        services.AddOptions<CrmOptions>().Bind(configuration.GetSection(CrmOptions.SectionName));
        services.AddSingleton<IValidateOptions<CrmOptions>, CrmOptionsValidator>();

        services.AddOptions<BiOptions>().Bind(configuration.GetSection(BiOptions.SectionName));
        services.AddSingleton<IValidateOptions<BiOptions>, BiOptionsValidator>();

        services.AddOptions<SapOptions>().Bind(configuration.GetSection(SapOptions.SectionName));
        services.AddSingleton<IValidateOptions<SapOptions>, SapOptionsValidator>();

        services.AddOptions<SmtpOptions>().Bind(configuration.GetSection(SmtpOptions.SectionName));
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();

        // Named clients so that timeouts and certificates are configured per target system
        // rather than globally (TR-INT-08).
        // TR-ARC-04 / TR-INT-02 / TR-INT-09: the handler puts the correlation ID on every
        // outbound call and logs its outcome and duration, so no adapter can forget to.
        services.AddTransient<CorrelationPropagationHandler>();

        services.AddHttpClient<ICrmGateway, CrmHttpGateway>(ConfigureCrmClient)
            .AddHttpMessageHandler<CorrelationPropagationHandler>();

        services.AddHttpClient<IBiGateway, BiGateway>(ConfigureBiClient)
            .AddHttpMessageHandler<CorrelationPropagationHandler>();

        services.AddHttpClient<ISapGateway, SapGateway>(ConfigureSapClient)
            .AddHttpMessageHandler<CorrelationPropagationHandler>();

        services.AddSingleton<IEmailGateway, SmtpEmailGateway>();

        return services;
    }

    /// <summary>Adapter option types validated eagerly at start-up.</summary>
    public static IReadOnlyList<Type> ValidatedOptionTypes { get; } =
    [
        typeof(CrmOptions),
        typeof(BiOptions),
        typeof(SapOptions),
        typeof(SmtpOptions)
    ];

    private static void ConfigureCrmClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<CrmOptions>>().Value;

        if (options.BaseAddress is not null)
        {
            client.BaseAddress = options.BaseAddress;
        }

        client.Timeout = options.Timeout;
    }

    private static void ConfigureBiClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<BiOptions>>().Value;

        if (options.BaseAddress is not null)
        {
            client.BaseAddress = options.BaseAddress;
        }

        client.Timeout = options.Timeout;
    }

    private static void ConfigureSapClient(IServiceProvider provider, HttpClient client)
    {
        var options = provider.GetRequiredService<IOptions<SapOptions>>().Value;

        if (options.BaseAddress is not null)
        {
            client.BaseAddress = options.BaseAddress;
        }

        client.Timeout = options.Timeout;
    }
}
