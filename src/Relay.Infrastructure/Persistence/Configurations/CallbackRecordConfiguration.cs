using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Infrastructure.Callbacks;

namespace Relay.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the callback audit log.
/// </summary>
internal sealed class CallbackRecordConfiguration : IEntityTypeConfiguration<CallbackRecord>
{
    public void Configure(EntityTypeBuilder<CallbackRecord> builder)
    {
        builder.ToTable("callback_records");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        // A plain string, not a ProviderId value object. For a rejected callback
        // the value is whatever the caller put in the URL, which may not be a
        // valid provider id at all — and an audit log that refuses to record
        // malformed input is missing exactly the rows worth having.
        builder.Property(c => c.ProviderId)
            .HasColumnName("provider_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(c => c.Disposition)
            .HasColumnName("disposition")
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        builder.Property(c => c.Detail).HasColumnName("detail").HasMaxLength(1_000);
        builder.Property(c => c.ReceiptCount).HasColumnName("receipt_count").IsRequired();
        builder.Property(c => c.AppliedCount).HasColumnName("applied_count").IsRequired();
        builder.Property(c => c.BodyPreview).HasColumnName("body_preview").HasMaxLength(2_000).IsRequired();
        builder.Property(c => c.ReceivedAt).HasColumnName("received_at").IsRequired();

        // The incident query: what did this provider send us, most recent first.
        builder.HasIndex(c => new { c.ProviderId, c.ReceivedAt })
            .HasDatabaseName("ix_callback_records_provider_received_at");

        // The security query: rejected callbacks over time. Partial, because
        // rejections should be a vanishing fraction of the table — and if they
        // ever are not, that is itself the thing worth seeing.
        builder.HasIndex(c => c.ReceivedAt)
            .HasDatabaseName("ix_callback_records_rejected")
            .HasFilter("disposition IN ('Rejected', 'Malformed', 'UnknownProvider')");
    }
}
