using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.Identity;
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

        // TRD 4 — access management (TR-SEC-02 to TR-SEC-07).
        services.AddOptions<PasswordPolicyOptions>()
            .Bind(configuration.GetSection(PasswordPolicyOptions.SectionName));
        services.AddSingleton<IValidateOptions<PasswordPolicyOptions>, PasswordPolicyOptionsValidator>();

        services.AddOptions<TwoFactorOptions>()
            .Bind(configuration.GetSection(TwoFactorOptions.SectionName));
        services.AddSingleton<IValidateOptions<TwoFactorOptions>, TwoFactorOptionsValidator>();

        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName));
        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();

        services.AddOptions<LockoutOptions>()
            .Bind(configuration.GetSection(LockoutOptions.SectionName));
        services.AddSingleton<IValidateOptions<LockoutOptions>, LockoutOptionsValidator>();

        // TR-ARC-04. Singleton because the value it holds is per-async-flow, not per-instance.
        services.AddSingleton<Abstractions.ICorrelationContext, CorrelationContext>();

        services.AddSingleton<Abstractions.Time.IClock, SystemClock>();

        // Pure cryptographic computation — no external system, no HTTP, no database driver — so
        // these live and are registered in this layer rather than in an infrastructure adapter
        // (TR-SEC-02, TR-SEC-04).
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddSingleton<IPasswordPolicyValidator, PasswordPolicyValidator>();
        services.AddSingleton<ITotpService, TotpService>();

        // Encrypts the TOTP secret at rest through ISecretResolver (TR-SEC-28); registered here
        // because it too is pure computation once the key is resolved, not an adapter.
        services.AddSingleton<ITotpSecretProtector, AesGcmTotpSecretProtector>();

        // Orchestrate ports declared in Abstractions.Persistence and Abstractions.Integration —
        // implemented in the Persistence and Integration layers respectively, never referenced
        // directly here (TR-ARC-01).
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IAdministrationService, AdministrationService>();

        // TRD 5 — activation request lifecycle (TR-ACT-01 to TR-ACT-19).
        services.AddScoped<IActivationRequestService, ActivationRequestService>();

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
        typeof(WorkingCalendarOptions),
        typeof(PasswordPolicyOptions),
        typeof(TwoFactorOptions),
        typeof(SessionOptions),
        typeof(LockoutOptions)
    ];
}
