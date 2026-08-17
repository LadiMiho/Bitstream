using Bitstream.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Bitstream.Infrastructure.Persistence.Configurations;

internal sealed class ActivationRequestConfiguration : IEntityTypeConfiguration<ActivationRequest>
{
    public void Configure(EntityTypeBuilder<ActivationRequest> builder)
    {
        builder.ToTable("ActivationRequest", Schemas.Portal);
        builder.HasKey(x => x.RequestId);

        builder.Property(x => x.RequestId).UseIdentityColumn();
        builder.Property(x => x.PublicId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.PackageCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.LocationRaw).HasMaxLength(1000).IsRequired();
        builder.Property(x => x.LocationLat).HasColumnType("decimal(9,6)");
        builder.Property(x => x.LocationLng).HasColumnType("decimal(9,6)");
        builder.Property(x => x.Classification).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Comments).HasMaxLength(2000);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.CrmTicketId).HasMaxLength(50);
        builder.Property(x => x.CrmCustomerId).HasMaxLength(50);
        builder.Property(x => x.Bp).HasMaxLength(50);
        builder.Property(x => x.SalesOrderId).HasMaxLength(50);
        builder.Property(x => x.FinancialCode).HasMaxLength(50);
        builder.Property(x => x.StatusReason).HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.LastUpdatedAt).HasColumnType("datetimeoffset(7)");

        // TR-DAT-04 / TR-DAT-05: unique, immutable and indexed for BI extraction.
        builder.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("UX_ActivationRequest_PublicId");
        builder.HasIndex(x => new { x.IspId, x.Status }).HasDatabaseName("IX_ActivationRequest_Isp_Status");
        builder.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_ActivationRequest_CreatedAt");
        builder.HasIndex(x => x.FinancialCode)
            .HasFilter("[FinancialCode] IS NOT NULL")
            .HasDatabaseName("IX_ActivationRequest_FinancialCode");
        builder.HasIndex(x => x.CrmTicketId)
            .HasFilter("[CrmTicketId] IS NOT NULL")
            .HasDatabaseName("IX_ActivationRequest_CrmTicketId");

        builder.HasOne(x => x.Isp)
            .WithMany(x => x.ActivationRequests)
            .HasForeignKey(x => x.IspId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ActiveLineConfiguration : IEntityTypeConfiguration<ActiveLine>
{
    public void Configure(EntityTypeBuilder<ActiveLine> builder)
    {
        builder.ToTable("ActiveLine", Schemas.Portal);
        builder.HasKey(x => x.LineId);

        builder.Property(x => x.LineId).UseIdentityColumn();
        builder.Property(x => x.ContractId).HasMaxLength(50).IsRequired();
        builder.Property(x => x.SubscriberReference).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Technology).HasMaxLength(30).IsRequired();
        builder.Property(x => x.PackageCode).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(40).IsRequired();
        builder.Property(x => x.BiSyncedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.BiChangeMarker).HasMaxLength(100);

        // TR-PAS-04: the sync is idempotent because the contract is unique per ISP.
        builder.HasIndex(x => new { x.IspId, x.ContractId }).IsUnique().HasDatabaseName("UX_ActiveLine_Isp_ContractId");

        // TR-PAS-05 / TR-NFR-05: server-side search of the line dropdown.
        builder.HasIndex(x => new { x.IspId, x.Technology, x.Status })
            .IncludeProperties(x => new { x.ContractId, x.SubscriberReference, x.PackageCode })
            .HasDatabaseName("IX_ActiveLine_Isp_Technology_Status");
        builder.HasIndex(x => x.SubscriberReference).HasDatabaseName("IX_ActiveLine_SubscriberReference");

        builder.HasOne(x => x.Isp)
            .WithMany(x => x.ActiveLines)
            .HasForeignKey(x => x.IspId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ComplaintTicketConfiguration : IEntityTypeConfiguration<ComplaintTicket>
{
    public void Configure(EntityTypeBuilder<ComplaintTicket> builder)
    {
        builder.ToTable("ComplaintTicket", Schemas.Portal);
        builder.HasKey(x => x.TicketId);

        builder.Property(x => x.TicketId).UseIdentityColumn();
        builder.Property(x => x.PublicId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.CategoryL1).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CategoryL2).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CategoryL3).HasMaxLength(50).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CrmTicketId).HasMaxLength(50);
        builder.Property(x => x.ClearingCode).HasMaxLength(50);
        builder.Property(x => x.ClearingText).HasMaxLength(2000);
        builder.Property(x => x.ClosureDecision).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ClosureDecisionAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.ConfirmationDueAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.LastAppliedEventAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.OpenedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.ClosedAt).HasColumnType("datetimeoffset(7)");

        builder.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("UX_ComplaintTicket_PublicId");
        builder.HasIndex(x => x.CrmTicketId)
            .HasFilter("[CrmTicketId] IS NOT NULL")
            .HasDatabaseName("IX_ComplaintTicket_CrmTicketId");

        // TR-PAS-31 / TR-PAS-32: dashboard filters served from indexed queries.
        builder.HasIndex(x => new { x.IspId, x.Status, x.OpenedAt }).HasDatabaseName("IX_ComplaintTicket_Isp_Status_OpenedAt");
        builder.HasIndex(x => x.LineId).HasDatabaseName("IX_ComplaintTicket_LineId");

        // TR-PAS-21h: administrator view of tickets awaiting confirmation, with remaining time.
        builder.HasIndex(x => x.ConfirmationDueAt)
            .HasFilter("[ConfirmationDueAt] IS NOT NULL")
            .HasDatabaseName("IX_ComplaintTicket_ConfirmationDueAt");

        builder.HasOne(x => x.Isp)
            .WithMany(x => x.ComplaintTickets)
            .HasForeignKey(x => x.IspId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.Line)
            .WithMany()
            .HasForeignKey(x => x.LineId)
            .OnDelete(DeleteBehavior.Restrict);

        // TR-PAS-21f: post-closure challenge links back to the original ticket.
        builder.HasOne(x => x.ParentTicket)
            .WithMany()
            .HasForeignKey(x => x.ParentTicketId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class TicketCommentConfiguration : IEntityTypeConfiguration<TicketComment>
{
    public void Configure(EntityTypeBuilder<TicketComment> builder)
    {
        builder.ToTable("TicketComment", Schemas.Portal);
        builder.HasKey(x => x.CommentId);

        builder.Property(x => x.CommentId).UseIdentityColumn();
        builder.Property(x => x.AuthorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.AuthorDisplayName).HasMaxLength(200);
        builder.Property(x => x.Body).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");
        builder.Property(x => x.CrmSyncStatus).HasMaxLength(20).IsRequired();
        builder.Property(x => x.CrmCommentId).HasMaxLength(50);

        builder.HasIndex(x => new { x.TicketId, x.CreatedAt }).HasDatabaseName("IX_TicketComment_Ticket_CreatedAt");

        // Deduplicates comments replicated from CRM (TR-PAS-26); filtered so portal-origin rows are exempt.
        builder.HasIndex(x => x.CrmCommentId)
            .IsUnique()
            .HasFilter("[CrmCommentId] IS NOT NULL")
            .HasDatabaseName("UX_TicketComment_CrmCommentId");

        builder.HasOne(x => x.Ticket)
            .WithMany(x => x.Comments)
            .HasForeignKey(x => x.TicketId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ServiceChangeRequestConfiguration : IEntityTypeConfiguration<ServiceChangeRequest>
{
    public void Configure(EntityTypeBuilder<ServiceChangeRequest> builder)
    {
        builder.ToTable("ServiceChangeRequest", Schemas.Portal);
        builder.HasKey(x => x.ChangeId);

        builder.Property(x => x.ChangeId).UseIdentityColumn();
        builder.Property(x => x.PublicId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ChangeType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.PackageAsIs).HasMaxLength(50).IsRequired();
        builder.Property(x => x.PackageToBe).HasMaxLength(50);
        builder.Property(x => x.RequestedTerminationDate).HasColumnType("date");
        builder.Property(x => x.Status).HasMaxLength(50).IsRequired();
        builder.Property(x => x.CrmReference).HasMaxLength(50);
        builder.Property(x => x.CreatedAt).HasColumnType("datetimeoffset(7)");

        builder.HasIndex(x => x.PublicId).IsUnique().HasDatabaseName("UX_ServiceChangeRequest_PublicId");
        builder.HasIndex(x => new { x.LineId, x.Status }).HasDatabaseName("IX_ServiceChangeRequest_Line_Status");

        builder.HasOne(x => x.Line)
            .WithMany()
            .HasForeignKey(x => x.LineId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
