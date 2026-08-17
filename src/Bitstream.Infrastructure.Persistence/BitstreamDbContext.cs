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
    /// <summary>Schema version this build is written against; see <c>ops.SchemaVersion</c>.</summary>
    public const int ExpectedSchemaVersion = 1;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BitstreamDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
