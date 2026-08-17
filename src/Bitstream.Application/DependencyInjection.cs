using Bitstream.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bitstream.Application;

/// <summary>
/// Registration entry point for the application layer.
/// Each layer exposes exactly one of these; Program.cs calls them in order and is the only
/// place in the solution that knows all four layers exist.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers application services and binds the platform configuration.
    /// <para>
    /// TR-ARC-06: package lists, classifications, notifiable statuses, identifier prefixes,
    /// closure timings and the holiday calendar are all configuration. None of them appears as
    /// a literal anywhere in the application layer.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBitstreamApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<IdentifierOptions>()
            .Bind(configuration.GetSection(IdentifierOptions.SectionName));
        services.AddSingleton<IValidateOptions<IdentifierOptions>, IdentifierOptionsValidator>();

        services.AddOptions<CatalogueOptions>()
            .Bind(configuration.GetSection(CatalogueOptions.SectionName));
        services.AddSingleton<IValidateOptions<CatalogueOptions>, CatalogueOptionsValidator>();

        services.AddOptions<TicketClosureOptions>()
            .Bind(configuration.GetSection(TicketClosureOptions.SectionName));
        services.AddSingleton<IValidateOptions<TicketClosureOptions>, TicketClosureOptionsValidator>();

        services.AddOptions<WorkingCalendarOptions>()
            .Bind(configuration.GetSection(WorkingCalendarOptions.SectionName));
        services.AddSingleton<IValidateOptions<WorkingCalendarOptions>, WorkingCalendarOptionsValidator>();

        // TR-ARC-04. Singleton because the value it holds is per-async-flow, not per-instance.
        services.AddSingleton<Abstractions.ICorrelationContext, CorrelationContext>();

        // Application service implementations are registered here as modules are built.
        return services;
    }

    /// <summary>
    /// Option types validated eagerly at start-up. The host walks this list so that a
    /// configuration mistake stops the deployment instead of surfacing on the first request
    /// that happens to need the value.
    /// </summary>
    public static IReadOnlyList<Type> ValidatedOptionTypes { get; } =
    [
        typeof(IdentifierOptions),
        typeof(CatalogueOptions),
        typeof(TicketClosureOptions),
        typeof(WorkingCalendarOptions)
    ];
}
