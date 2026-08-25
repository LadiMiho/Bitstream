using Bitstream.Application.Abstractions.Integration;
using Bitstream.Application.Abstractions.Persistence;
using Bitstream.Application.Identity.Entities;
using Bitstream.Application.Services.Identity;
using Bitstream.Infrastructure.Persistence.HealthChecks;
using Bitstream.Infrastructure.Persistence.Identity;
using Bitstream.Infrastructure.Persistence.Repositories;
#pragma warning disable IDE0005 // dotnet-format's analyzer misreports this as unused (see AddIdentity<User, Role>() below) — CI has proven the build fails without it.
using Microsoft.AspNetCore.Identity;
#pragma warning restore IDE0005
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

        // Same physical database, same connection string — a separate DbContext only because it
        // is the one part of the schema that is EF-migration-owned (BitstreamIdentityDbContext's
        // own doc comment explains why).
        services.AddDbContext<BitstreamIdentityDbContext>((provider, builder) =>
        {
            var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName);

            builder.UseSqlServer(connectionString, sql =>
            {
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

        // Encrypts BitstreamIdentityDbContext's UserTokens.Value column (the TOTP authenticator
        // key, among other Identity-generated tokens) at rest. Explicit even though AddIdentity
        // below would register it too, so this registration is never load-bearing on ordering.
        services.AddDataProtection();

        // User/role credential storage and CRUD, lockout (TR-SEC-06/12), and two-factor
        // (TR-SEC-04) all run through ASP.NET Core Identity's own EF store
        // (AddEntityFrameworkStores<BitstreamIdentityDbContext>) — genuinely, not decoratively:
        // dbo.Users/Roles/etc. are real, EF-migration-owned tables (BitstreamIdentityDbContext),
        // not a hand-written-schema bridge, and every one of Identity's own subsystems is used as
        // designed rather than re-implemented. AddIdentity (not AddIdentityCore) additionally
        // wires SignInManager and the cookie authentication scheme Bitstream.Web/Program.cs
        // configures. Argon2IdentityPasswordHasher still keeps TR-SEC-02 (Argon2id specifically)
        // rather than accepting Identity's PBKDF2 default.
        services.AddIdentity<User, Role>(options =>
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

            // TR-SEC-06: 5 consecutive failed attempts (password or 2FA code — SignInManager
            // counts both against the same AccessFailedCount). No auto-expiry: today's design has
            // never had one either — only an administrator unlocks an account
            // (AdministrationService.SetUserLockedAsync).
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromDays(36500);
            options.Lockout.AllowedForNewUsers = true;
        })
            .AddRoles<Role>()
            .AddEntityFrameworkStores<BitstreamIdentityDbContext>()
            .AddDefaultTokenProviders();

        // IdentityBuilder has no AddPasswordHasher fluent method — AddIdentityCore registers the
        // default IPasswordHasher<User> with TryAddScoped, so a plain AddScoped registered after
        // it wins on resolution (DI resolves the last registration for a non-collection
        // dependency), overriding it with Argon2IdentityPasswordHasher.
        services.AddScoped<Microsoft.AspNetCore.Identity.IPasswordHasher<User>, Argon2IdentityPasswordHasher>();

        // TRD 5 — activation request lifecycle. The identifier generator and the outbox are
        // both persistence-backed (no adapter, no HttpClient): the former calls a stored
        // procedure, the latter only stores and claims rows for a dispatcher that does not exist
        // yet (Phase 4).
        services.AddScoped<IActivationRequestRepository, ActivationRequestRepository>();
        services.AddScoped<IActivationCatalogueRepository, ActivationCatalogueRepository>();
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
