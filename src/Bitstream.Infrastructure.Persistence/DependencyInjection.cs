using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Services.Identity;
using Bitstream.Domain.Entities;
using Bitstream.Infrastructure.Persistence.HealthChecks;
using Bitstream.Infrastructure.Persistence.Identity;
using Bitstream.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// Registration entry point for the persistence layer.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the EF Core context. The connection string is named by
    /// <see cref="DatabaseOptions.ConnectionStringName"/> and supplied per environment; its
    /// credential comes from the secret store or from Integrated Security, never from a
    /// checked-in file (TR-SEC-28).
    /// </summary>
    public static IServiceCollection AddBitstreamPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName));
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddDbContext<BitstreamDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

            builder.UseSqlServer(connectionString, sql =>
            {
                // TR-NFR-07: transient faults are retried rather than surfaced to the user.
                sql.EnableRetryOnFailure(
                    maxRetryCount: options.MaxRetryCount,
                    maxRetryDelay: options.MaxRetryDelay,
                    errorNumbersToAdd: null);
                sql.CommandTimeout(options.CommandTimeoutSeconds);
            });
        });

        // Deployed schema versus expected schema, checked once at start-up (ADR-0002).
        services.AddHostedService<SchemaVersionGuard>();

        // TRD 4 — identity and access data access, and the unit of work and audit writer every
        // application service in that module depends on. Scoped: each tracks changes on the
        // same per-request BitstreamDbContext instance (also scoped), so a repository's tracked
        // entity and IUnitOfWork.SaveChangesAsync agree on what is being persisted.
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IIspRepository, IspRepository>();
        services.AddScoped<IUserSessionStore, UserSessionStore>();
        services.AddScoped<ITwoFactorChallengeStore, TwoFactorChallengeStore>();

        // User/role credential storage and CRUD now runs through ASP.NET Core Identity's
        // UserManager<User>/RoleManager<Role> — genuinely, not decoratively: BitstreamUserStore/
        // BitstreamRoleStore bridge the existing hand-written schema (ADR-0002, no EF
        // migrations) to Identity's contract, and Argon2IdentityPasswordHasher keeps TR-SEC-02
        // (Argon2id specifically) rather than accepting Identity's PBKDF2 default. AddIdentityCore
        // (not AddIdentity) deliberately excludes Identity's own cookie authentication and
        // lockout store: sessions stay the custom UserSessionStore above (TR-SEC-07, kept so
        // "list active sessions" / "revoke all" keep working), and lockout stays
        // User.FailedLoginCount/UserStatus (TR-SEC-06, IdentityService's business rule).
        services.AddIdentityCore<User>(options =>
        {
            // IPasswordPolicyValidator already enforces the real policy (TR-SEC-03) before
            // UserManager is ever called; Identity's own password/user validators would
            // otherwise duplicate that check and could disagree with it.
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 0;
            options.Password.RequiredUniqueChars = 0;
            options.User.RequireUniqueEmail = false;
            options.Lockout.AllowedForNewUsers = false;
        })
            .AddRoles<Role>()
            .AddUserStore<BitstreamUserStore>()
            .AddRoleStore<BitstreamRoleStore>()
            .AddPasswordHasher<Argon2IdentityPasswordHasher>();

        // TRD 5 — activation request lifecycle. The identifier generator and the outbox are
        // both persistence-backed (no adapter, no HttpClient): the former calls a stored
        // procedure, the latter only stores and claims rows for a dispatcher that does not exist
        // yet (Phase 4).
        services.AddScoped<IActivationRequestRepository, ActivationRequestRepository>();
        services.AddScoped<IPublicIdentifierGenerator, SqlPublicIdentifierGenerator>();
        services.AddScoped<IIntegrationOutbox, IntegrationOutbox>();

        // TRD 6 — post-activation support.
        services.AddScoped<IActiveLineRepository, ActiveLineRepository>();
        services.AddScoped<IComplaintTicketRepository, ComplaintTicketRepository>();
        services.AddScoped<IServiceChangeRequestRepository, ServiceChangeRequestRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<ISyncStateStore, SyncStateStore>();

        return services;
    }

    /// <summary>
    /// Registers the database reachability check (TR-ARC-05). Tagged "database" so that
    /// readiness includes it and liveness does not.
    /// </summary>
    public static IHealthChecksBuilder AddBitstreamPersistenceHealthChecks(this IHealthChecksBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: ["dependency", "database"]);
    }

    /// <summary>Persistence option types validated eagerly at start-up.</summary>
    public static IReadOnlyList<Type> ValidatedOptionTypes { get; } = [typeof(DatabaseOptions)];
}
