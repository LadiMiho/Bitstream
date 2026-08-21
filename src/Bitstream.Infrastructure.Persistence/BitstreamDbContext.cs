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
/// </summary>
public sealed class BitstreamDbContext : DbContext
{
    /// <summary>
    /// Schema version this build is written against; see <c>ops.SchemaVersion</c>. Bumped from 1
    /// to 2 when db/mssql/0009_sessions_and_two_factor.sql (TRD 4 access management) was added,
    /// from 2 to 3 when db/mssql/0010_activation_event_ordering.sql (TRD 7.3.2) was added, from
    /// 3 to 4 when db/mssql/0011_post_activation_support.sql (TRD 6) was added, and from 4 to 5
    /// when db/mssql/0012_totp_enrollment.sql (TR-SEC-04, first-login QR enrollment) was added.
    /// </summary>
    public const int ExpectedSchemaVersion = 5;

    public BitstreamDbContext(DbContextOptions<BitstreamDbContext> options)
        : base(options)
    {
    }

    public DbSet<Isp> Isps => Set<Isp>();

    public DbSet<User> Users => Set<User>();

    public DbSet<UserPasswordHistory> UserPasswordHistory => Set<UserPasswordHistory>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<TwoFactorChallenge> TwoFactorChallenges => Set<TwoFactorChallenge>();

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
