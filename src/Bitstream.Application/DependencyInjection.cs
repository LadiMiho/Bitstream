using Bitstream.Application.Abstractions.Security;
using Bitstream.Application.Configuration;
using Bitstream.Application.Services;
using Bitstream.Application.Services.Activation;
using Bitstream.Application.Services.Identity;
using Bitstream.Application.Services.Integration;
using Bitstream.Application.Services.PostActivation;
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

        // Orchestrate ports declared in Abstractions.Persistence and Abstractions.Integration —
        // implemented in the Persistence and Integration layers respectively, never referenced
        // directly here (TR-ARC-01). Login/2FA/session orchestration itself now lives in
        // Bitstream.Web (SignInManager needs HttpContext) — no IIdentityService registration here.
        services.AddScoped<IAdministrationService, AdministrationService>();

        // TRD 5 — activation request lifecycle (TR-ACT-01 to TR-ACT-19).
        services.AddScoped<IActivationRequestService, ActivationRequestService>();

        // TRD 7.3 — CRM integration. Direction A: the dispatcher claims the outbox and calls
        // ICrmGateway (Integration layer). Direction B: InboundEventService interprets a
        // persisted inbound event (TR-ARC-03, TR-INT-22 to TR-INT-31).
        services.AddOptions<OutboxDispatcherOptions>()
            .Bind(configuration.GetSection(OutboxDispatcherOptions.SectionName));
        services.AddSingleton<IValidateOptions<OutboxDispatcherOptions>, OutboxDispatcherOptionsValidator>();

        // Registered as its own singleton, not only as IHostedService, so a test can resolve it
        // directly and call DispatchBatchAsync deterministically instead of racing the poll timer.
        // The IHostedService registration that actually runs it is AddBitstreamBackgroundJobs,
        // which exactly one host calls — see the remarks there.
        services.AddSingleton<OutboxDispatcher>();

        services.AddScoped<IInboundEventService, InboundEventService>();

        // TRD 6 — post-activation support.
        services.AddOptions<ActiveLineSyncOptions>()
            .Bind(configuration.GetSection(ActiveLineSyncOptions.SectionName));

        services.AddSingleton<IWorkingDayCalculator, WorkingDayCalculator>();
        services.AddScoped<IActiveLineSyncService, ActiveLineSyncService>();
        services.AddScoped<IComplaintTicketService, ComplaintTicketService>();
        services.AddScoped<ITicketClosureService, TicketClosureService>();
        services.AddScoped<IServiceChangeRequestService, ServiceChangeRequestService>();
        services.AddScoped<INotificationService, NotificationService>();

        return services;
    }

    /// <summary>
    /// Starts the recurring background work: the outbox dispatcher (TR-ARC-03), the BI
    /// active-lines sync (TR-PAS-03) and the auto-confirmation sweep (TR-PAS-21).
    /// <para>
    /// <b>Exactly one host may call this.</b> These jobs are not idempotent with respect to
    /// each other running twice at the same moment: two dispatchers would each claim and send
    /// the same outbox message, and two sweeps would each auto-confirm the same ticket. The
    /// portal site (<c>Bitstream.Web</c>) therefore registers the services but not the jobs,
    /// and the integration host (<c>Bitstream.Api</c>) — which is the one that talks to CRM —
    /// runs them. That also keeps the split honest: a request a user submits on the portal is
    /// dispatched to CRM by the host that owns CRM communication, not by the one serving pages.
    /// </para>
    /// <para>
    /// It follows that the API host must be deployed for outbound CRM traffic to move at all.
    /// A portal-only deployment accepts submissions and queues them, and they sit on the outbox
    /// until an API host exists to drain it.
    /// </para>
    /// </summary>
    public static IServiceCollection AddBitstreamBackgroundJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService(provider => provider.GetRequiredService<OutboxDispatcher>());
        services.AddHostedService<ActiveLineSyncScheduler>();
        services.AddHostedService<AutoConfirmationSweepScheduler>();

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
        typeof(LockoutOptions),
        typeof(OutboxDispatcherOptions)
    ];
}
