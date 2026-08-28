using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Infrastructure.Persistence.Outbox;

namespace Relay.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the outbox.
/// </summary>
internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(o => o.Type)
            .HasColumnName("type")
            .HasMaxLength(512)
            .IsRequired();

        // jsonb rather than text. The payload is queried during incidents — "which
        // events for this message are still pending" — and jsonb makes that a
        // query instead of an export. It also rejects malformed JSON on write,
        // which turns a serialization bug into a failed insert rather than a row
        // that fails years later when something tries to read it.
        builder.Property(o => o.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(o => o.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(o => o.ProcessedAt).HasColumnName("processed_at");
        builder.Property(o => o.AttemptCount).HasColumnName("attempt_count").IsRequired();
        builder.Property(o => o.LastError).HasColumnName("last_error").HasMaxLength(4_000);

        // The dispatcher's only query: unprocessed rows, oldest first.
        //
        // Partial, on purpose. This table is append-mostly and the processed rows
        // vastly outnumber the pending ones within minutes of any burst. An index
        // over the whole table would grow forever while only ever being used to
        // find the handful of rows at its edge.
        builder.HasIndex(o => o.OccurredAt)
            .HasDatabaseName("ix_outbox_messages_pending")
            .HasFilter("processed_at IS NULL");
    }
}
