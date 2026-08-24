using Bitstream.Application.Identity.Entities;
using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bitstream.Infrastructure.Persistence;

/// <summary>
/// EF Core model over the Bitstream portal database.
/// <para>
/// The physical schema is owned by the numbered T-SQL scripts under <c>/db/mssql</c>
/// (ADR-0002). This context maps onto that schema and never creates it: there are no
/// migrations, and <c>EnsureCreated</c> must never be called. <c>SchemaGuard</c> compares
/// the deployed schema version against <see cref="ExpectedSchemaVersion"/> at start-up so
/// that drift fails fast instead of silently.
/// </para>
/// <para>
/// <see cref="Users"/>/<see cref="Roles"/> are the one deliberate exception to "never creates
/// it": their physical table (<c>dbo.Users</c>/<c>Roles</c>) is migration-owned by
/// <see cref="Identity.BitstreamIdentityDbContext"/>, not this context. They are mapped here,
/// read/write, purely so <c>RolePermission</c>/<c>UserPasswordHistory</c> — still hand-written,
/// unmigrated tables — can <c>.Include()</c> across to them in one query, exactly as before this
/// migration existed.
/// </para>
/// </summary>
public sealed class BitstreamDbContext : DbContext
{
    /// <summary>
    /// Schema version this build is written against; see <c>ops.SchemaVersion</c>. Bumped from 1
    /// to 2 when db/mssql/0009_sessions_and_two_factor.sql (TRD 4 access management) was added,
    /// from 2 to 3 when db/mssql/0010_activation_event_ordering.sql (TRD 7.3.2) was added, from
    /// 3 to 4 when db/mssql/0011_post_activation_support.sql (TRD 6) was added, from 4 to 5
    /// when db/mssql/0012_totp_enrollment.sql (TR-SEC-04, first-login QR enrollment) was added,
    /// from 5 to 6 when db/mssql/0013_user_deleted_status.sql (soft-delete for User
    /// Administration) was added, from 6 to 7 when db/mssql/0014_drop_legacy_identity_tables.sql
    /// (sec.[User]/sec.Role dropped in favour of the EF-migrated Users/Roles) was added, from 7
    /// to 8 when db/mssql/0015_seed_role_baseline.sql (role seeding, split out of 0007 so it can
    /// run after 0014 has re-pointed sec.RolePermission at dbo.Roles) was added, and from 8 to 9
    /// when db/mssql/0016_drop_session_and_twofactor_tables.sql (sec.UserSession/TwoFactorChallenge
    /// dropped — both fully superseded by ASP.NET Core Identity's own cookie auth and 2FA token
    /// providers) was added.
    /// </summary>
    public const int ExpectedSchemaVersion = 9;

    public BitstreamDbContext(DbContextOptions<BitstreamDbContext> options)
        : base(options)
    {
    }

    public DbSet<Isp> Isps => Set<Isp>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPasswordHistory> UserPasswordHistory => Set<UserPasswordHistory>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<ActivationRequest> ActivationRequests => Set<ActivationRequest>();

    public DbSet<ActiveLine> ActiveLines => Set<ActiveLine>();

    public DbSet<ComplaintTicket> ComplaintTickets => Set<ComplaintTicket>();

    public DbSet<TicketComment> TicketComments => Set<TicketComment>();

    public DbSet<ServiceChangeRequest> ServiceChangeRequests => Set<ServiceChangeRequest>();

    public DbSet<Notification> Notifications => Set<Notification>();

    /// <summary>Append-only (TR-SEC-24). Written through <c>IAuditWriter</c>; never updated or removed.</summary>
    public DbSet<AuditLog> AuditLog => Set<AuditLog>();

    public DbSet<IntegrationMessage> IntegrationMessages => Set<IntegrationMessage>();

    public DbSet<SyncState> SyncStates => Set<SyncState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BitstreamDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
