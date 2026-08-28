using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps <see cref="DeliveryAttempt"/>, which lives inside the message aggregate.
/// </summary>
/// <remarks>
/// Configured, but not exposed: there is no <c>DbSet&lt;DeliveryAttempt&gt;</c>
/// and no repository. An attempt is reachable only through its message, because
/// an attempt without one is not a meaningful thing to load.
/// </remarks>
internal sealed class DeliveryAttemptConfiguration : IEntityTypeConfiguration<DeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<DeliveryAttempt> builder)
    {
        builder.ToTable("delivery_attempts");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();

        // The foreign key back to the message is a shadow property: DeliveryAttempt
        // has no MessageId of its own, because inside an aggregate a child does not
        // need a handle on its parent — it is only ever reached through it.
        //
        // Declared explicitly, with its type and conversion, rather than left for
        // EF to infer. The index below references it by name, and a name-only
        // reference to a property that does not exist yet creates an untyped shadow
        // property instead, which then fails model validation.
        builder.Property<MessageId>("message_id")
            .HasConversion(id => id.Value, value => new MessageId(value))
            .HasColumnName("message_id")
            .IsRequired();

        builder.Property(a => a.Sequence)
            .HasColumnName("sequence")
            .IsRequired();

        builder.Property(a => a.ProviderId)
            .HasConversion(
                id => id.Value,
                value => ProviderId.Create(value).Value)
            .HasColumnName("provider_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.Outcome)
            .HasColumnName("outcome")
            .HasConversion<string>()
            .HasMaxLength(24)
            .IsRequired();

        builder.Property(a => a.ProviderMessageId)
            .HasColumnName("provider_message_id")
            .HasMaxLength(256);

        builder.Property(a => a.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(2_000);

        builder.Property(a => a.Duration)
            .HasColumnName("duration")
            .IsRequired();

        builder.Property(a => a.AttemptedAt)
            .HasColumnName("attempted_at")
            .IsRequired();

        // How an inbound delivery receipt finds its message. The provider is part
        // of the key because two providers can, and eventually will, hand out the
        // same identifier.
        //
        // Not unique: a receipt is matched to the most recent attempt, and a
        // provider re-issuing an identifier after its own retention window has
        // elapsed is a nuisance rather than a corruption. A unique index here
        // would turn that nuisance into a failed insert on a healthy path.
        builder.HasIndex(a => new { a.ProviderId, a.ProviderMessageId })
            .HasDatabaseName("ix_delivery_attempts_provider_message_id")
            .HasFilter("provider_message_id IS NOT NULL");

        builder.HasIndex("message_id", nameof(DeliveryAttempt.Sequence))
            .IsUnique()
            .HasDatabaseName("ix_delivery_attempts_message_sequence");
    }
}
