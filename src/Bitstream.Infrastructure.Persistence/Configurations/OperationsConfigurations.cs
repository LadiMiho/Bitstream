using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bitstream.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notification", Schemas.Operations);
        builder.HasKey(x => x.NotificationId);

        builder.Property(x => x.NotificationId).UseIdentityColumn();
        builder.Property(x => x.TemplateCode).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Recipients).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(500).IsRequired();
        builder.Property(x => x.BodyRendered).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.RelatedEntityType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.RelatedEntityPublicId).HasMaxLength(32);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.SentAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.CorrelationId).HasMaxLength(64);

        builder.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("IX_Notification_Status_CreatedAt");
        builder.HasIndex(x => new { x.RelatedEntityType, x.RelatedEntityId }).HasDatabaseName("IX_Notification_RelatedEntity");
    }
}

internal sealed class IntegrationMessageConfiguration : IEntityTypeConfiguration<IntegrationMessage>
{
    public void Configure(EntityTypeBuilder<IntegrationMessage> builder)
    {
        builder.ToTable("IntegrationMessage", Schemas.Operations);
        builder.HasKey(x => x.MessageId);

        builder.Property(x => x.MessageId).UseIdentityColumn();
        builder.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.TargetSystem).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(x => x.InterfaceCode).HasMaxLength(30).IsRequired();
        builder.Property(x => x.MessageType).HasMaxLength(50);
        builder.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
        builder.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(2000);
        builder.Property(x => x.NextRetryAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.RelatedPublicId).HasMaxLength(32);
        builder.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.ProcessedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.ResponsePayload).HasColumnType("nvarchar(max)");

        // TR-INT-03 / TR-INT-25: one row per idempotency key per direction and target system.
        // Inbound this is the CRM eventId; outbound it is the public identifier plus interface.
        builder.HasIndex(x => new { x.Direction, x.TargetSystem, x.InterfaceCode, x.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("UX_IntegrationMessage_Idempotency");

        // Dispatcher claim query (TR-ARC-03) and dead-letter listing (TR-INT-05).
        builder.HasIndex(x => new { x.Status, x.NextRetryAt }).HasDatabaseName("IX_IntegrationMessage_Status_NextRetryAt");
        builder.HasIndex(x => x.RelatedPublicId).HasDatabaseName("IX_IntegrationMessage_RelatedPublicId");
        builder.HasIndex(x => x.CorrelationId).HasDatabaseName("IX_IntegrationMessage_CorrelationId");
    }
}

internal sealed class SyncStateConfiguration : IEntityTypeConfiguration<SyncState>
{
    public void Configure(EntityTypeBuilder<SyncState> builder)
    {
        builder.ToTable("SyncState", Schemas.Operations);
        builder.HasKey(x => x.SyncKey);

        builder.Property(x => x.SyncKey).HasMaxLength(50);
        builder.Property(x => x.LastRunAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.LastSuccessfulSyncAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.ChangeMarker).HasMaxLength(200);
    }
}
