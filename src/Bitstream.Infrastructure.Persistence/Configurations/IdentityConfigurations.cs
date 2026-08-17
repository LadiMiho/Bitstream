using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bitstream.Infrastructure.Persistence.Configurations;

/// <summary>Schema and column names shared by the configurations, kept in one place.</summary>
internal static class Schemas
{
    /// <summary>Identity, access control and audit.</summary>
    public const string Security = "sec";

    /// <summary>Business records visible to ISPs.</summary>
    public const string Portal = "portal";

    /// <summary>Operational stores: outbox/inbox, notifications, counters.</summary>
    public const string Operations = "ops";
}

internal sealed class IspConfiguration : IEntityTypeConfiguration<Isp>
{
    public void Configure(EntityTypeBuilder<Isp> builder)
    {
        builder.ToTable("Isp", Schemas.Security);
        builder.HasKey(x => x.IspId);

        builder.Property(x => x.IspId).UseIdentityColumn();
        builder.Property(x => x.Name).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Nipt).HasMaxLength(20).IsRequired();
        builder.Property(x => x.ContactPerson).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ContactEmail).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ContactMobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CrmBpReference).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");

        // TR-SEC-15/16: NIPT unique across the platform.
        builder.HasIndex(x => x.Nipt).IsUnique().HasDatabaseName("UX_Isp_Nipt");
        builder.HasIndex(x => x.CrmBpReference).HasDatabaseName("IX_Isp_CrmBpReference");
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("User", Schemas.Security);
        builder.HasKey(x => x.UserId);

        builder.Property(x => x.UserId).UseIdentityColumn();
        builder.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Email).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Mobile).HasMaxLength(20).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(x => x.PasswordHashAlgorithm).HasMaxLength(50).IsRequired();
        builder.Property(x => x.TotpSecret).HasColumnType("varbinary(256)");
        builder.Property(x => x.LastLoginAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.PasswordUpdatedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");

        // TR-SEC-01 / TR-SEC-14: email unique across the entire platform, internal users included.
        builder.HasIndex(x => x.Email).IsUnique().HasDatabaseName("UX_User_Email");
        builder.HasIndex(x => x.IspId).HasFilter("[IspId] IS NOT NULL").HasDatabaseName("IX_User_IspId");

        builder.HasOne(x => x.Isp)
            .WithMany(x => x.Users)
            .HasForeignKey(x => x.IspId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Role)
            .WithMany(x => x.Users)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class UserPasswordHistoryConfiguration : IEntityTypeConfiguration<UserPasswordHistory>
{
    public void Configure(EntityTypeBuilder<UserPasswordHistory> builder)
    {
        builder.ToTable("UserPasswordHistory", Schemas.Security);
        builder.HasKey(x => x.PasswordHistoryId);

        builder.Property(x => x.PasswordHistoryId).UseIdentityColumn();
        builder.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
        builder.Property(x => x.PasswordHashAlgorithm).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");

        builder.HasIndex(x => new { x.UserId, x.CreatedAt }).HasDatabaseName("IX_UserPasswordHistory_UserId_CreatedAt");

        builder.HasOne(x => x.User)
            .WithMany(x => x.PasswordHistory)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("Role", Schemas.Security);
        builder.HasKey(x => x.RoleId);

        builder.Property(x => x.RoleId).UseIdentityColumn();
        builder.Property(x => x.Name).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);

        builder.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_Role_Name");
    }
}

internal sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("Permission", Schemas.Security);
        builder.HasKey(x => x.PermissionId);

        builder.Property(x => x.PermissionId).UseIdentityColumn();
        builder.Property(x => x.Code).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);

        builder.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_Permission_Code");
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("RolePermission", Schemas.Security);
        builder.HasKey(x => new { x.RoleId, x.PermissionId });

        builder.Property(x => x.GrantedAt).HasColumnType("datetimeoffset(7)");

        builder.HasOne(x => x.Role)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Permission)
            .WithMany(x => x.RolePermissions)
            .HasForeignKey(x => x.PermissionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLog", Schemas.Security);
        builder.HasKey(x => x.AuditId);

        builder.Property(x => x.AuditId).UseIdentityColumn();
        builder.Property(x => x.Timestamp).HasColumnType("datetimeoffset(7)").IsRequired();
        builder.Property(x => x.ActorIp).HasMaxLength(64);
        builder.Property(x => x.ActionCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityId).HasMaxLength(64);
        builder.Property(x => x.OldValue).HasColumnType("nvarchar(max)");
        builder.Property(x => x.NewValue).HasColumnType("nvarchar(max)");
        builder.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();

        // TR-SEC-25: searchable by date, actor, action type.
        builder.HasIndex(x => x.Timestamp).HasDatabaseName("IX_AuditLog_Timestamp");
        builder.HasIndex(x => new { x.ActorUserId, x.Timestamp }).HasDatabaseName("IX_AuditLog_Actor_Timestamp");
        builder.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("IX_AuditLog_Entity");
        builder.HasIndex(x => x.CorrelationId).HasDatabaseName("IX_AuditLog_CorrelationId");
    }
}
