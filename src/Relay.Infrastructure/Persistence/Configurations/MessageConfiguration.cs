using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Persistence.Configurations;

/// <summary>
/// Maps the <see cref="Message"/> aggregate.
/// </summary>
/// <remarks>
/// The point of this file is that the domain model above it did not have to
/// change to be persistable. No public setters were added, no parameterless
/// public constructor, no primitive stand-ins for the value objects. EF Core
/// reaches the private members through backing fields and private constructors,
/// which is what makes ADR 0004's claim — that the model does not bend for the
/// ORM — checkable rather than aspirational.
/// </remarks>
internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id)
            .HasConversion(id => id.Value, value => new MessageId(value))
            .HasColumnName("id")
            .ValueGeneratedNever();

        ConfigureIdempotency(builder);
        ConfigureRecipient(builder);
        ConfigureBody(builder);
        ConfigureLifecycle(builder);
        ConfigureAttempts(builder);
        ConfigureConcurrency(builder);

        // Domain events live in memory between a transition and the commit that
        // drains them. They are never a column.
        builder.Ignore(m => m.DomainEvents);

        // Derived from Recipient. Persisting it too would create a second source
        // of truth for the same fact, and eventually the two would disagree.
        builder.Ignore(m => m.Channel);
    }

    private static void ConfigureIdempotency(EntityTypeBuilder<Message> builder)
    {
        builder.Property(m => m.IdempotencyKey)
            .HasConversion(
                key => key.Value,

                // Create returns a Result, and reading .Value throws when the
                // stored string is not a valid key. That is the right behaviour
                // here: a row that cannot produce its own value object means the
                // database has been written to by something that bypassed the
                // domain, and continuing would spread the corruption.
                value => IdempotencyKey.Create(value).Value)
            .HasMaxLength(IdempotencyKey.MaxLength)
            .HasColumnName("idempotency_key")
            .IsRequired();

        // The guarantee behind ADR 0008.
        //
        // Duplicate suppression is enforced here, by the storage engine, and not
        // by a lookup in the handler. A check-then-insert has a window between the
        // two halves, and two concurrent submissions of the same key both find
        // nothing and both insert. This index has no window: one of them fails.
        //
        // It is also why IdempotencyKey normalises at construction — the index
        // compares bytes, so "Order-1 " and "order-1" would be two keys without it.
        builder.HasIndex(m => m.IdempotencyKey)
            .IsUnique()
            .HasDatabaseName("ix_messages_idempotency_key");
    }

    private static void ConfigureRecipient(EntityTypeBuilder<Message> builder)
    {
        // Owned rather than converted to a single string, because a recipient has
        // two components and the channel half is queried on its own.
        builder.OwnsOne(m => m.Recipient, recipient =>
        {
            recipient.Property(r => r.Channel)
                .HasColumnName("channel")
                .HasConversion<string>()
                .HasMaxLength(16)
                .IsRequired();

            recipient.Property(r => r.Address)
                .HasColumnName("recipient_address")
                .HasMaxLength(512)
                .IsRequired();
        });

        builder.Navigation(m => m.Recipient).IsRequired();
    }

    private static void ConfigureBody(EntityTypeBuilder<Message> builder)
    {
        builder.OwnsOne(m => m.Body, body =>
        {
            body.Property(b => b.Content)
                .HasColumnName("body")
                .HasMaxLength(MessageBody.EmailMaxLength)
                .IsRequired();

            body.Property(b => b.Subject)
                .HasColumnName("subject")
                .HasMaxLength(MessageBody.SubjectMaxLength);
        });

        builder.Navigation(m => m.Body).IsRequired();
    }

    private static void ConfigureLifecycle(EntityTypeBuilder<Message> builder)
    {
        // Stored as text, not as the underlying integer.
        //
        // An enum persisted by ordinal is a trap: reordering the members silently
        // rewrites the meaning of every existing row, and nothing fails until
        // someone reads one. Text costs a few bytes and makes the column readable
        // in a query, which is where it will actually be looked at.
        builder.Property(m => m.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(m => m.MaxAttempts)
            .HasColumnName("max_attempts")
            .IsRequired();

        builder.Property(m => m.CurrentProviderId)
            .HasConversion(
                id => id!.Value,
                value => ProviderId.Create(value).Value)
            .HasColumnName("current_provider_id")
            .HasMaxLength(64);

        builder.Property(m => m.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(2_000);

        builder.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(m => m.DispatchStartedAt).HasColumnName("dispatch_started_at");
        builder.Property(m => m.SentAt).HasColumnName("sent_at");
        builder.Property(m => m.CompletedAt).HasColumnName("completed_at");

        // The pipeline's hot query: oldest pending message first.
        builder.HasIndex(m => new { m.Status, m.CreatedAt })
            .HasDatabaseName("ix_messages_status_created_at");

        // The sweeper's two queries. Partial indexes, because both look at a small
        // and shrinking slice — messages awaiting a receipt, and messages stuck
        // mid-dispatch — while the table as a whole is mostly terminal rows that
        // will never match either.
        builder.HasIndex(m => m.SentAt)
            .HasDatabaseName("ix_messages_awaiting_receipt")
            .HasFilter("status = 'Sent'");

        builder.HasIndex(m => m.DispatchStartedAt)
            .HasDatabaseName("ix_messages_stuck_dispatching")
            .HasFilter("status = 'Dispatching'");
    }

    private static void ConfigureAttempts(EntityTypeBuilder<Message> builder)
    {
        builder.HasMany(m => m.Attempts)
            .WithOne()
            .HasForeignKey("message_id")
            .OnDelete(DeleteBehavior.Cascade);

        // Written through the private list, not through the read-only property EF
        // would otherwise try to use. This is what lets Attempts stay
        // IReadOnlyList<T> in the domain while still being a mapped collection.
        builder.Metadata
            .FindNavigation(nameof(Message.Attempts))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }

    private static void ConfigureConcurrency(EntityTypeBuilder<Message> builder)
    {
        // Optimistic concurrency over PostgreSQL's own row version.
        //
        // `xmin` is a system column every row already has — the id of the
        // transaction that last wrote it — so this costs no storage and cannot get
        // out of step with the row the way a manually incremented counter can.
        // Mapping the aggregate's uint Version onto it is why that property is a
        // uint rather than a byte[].
        //
        // What it buys: two workers claiming the same message both succeed at
        // BeginDispatch in memory, and exactly one of them succeeds at the commit.
        // The other gets a ConcurrencyConflictException and stops — instead of both
        // of them sending.
        builder.Property(m => m.Version)
            .IsConcurrencyToken()
            .ValueGeneratedOnAddOrUpdate();
    }
}
