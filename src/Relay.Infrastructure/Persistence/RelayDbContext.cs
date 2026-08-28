using Microsoft.EntityFrameworkCore;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Persistence.Outbox;

namespace Relay.Infrastructure.Persistence;

/// <summary>
/// The database.
/// </summary>
/// <remarks>
/// One <see cref="DbSet{TEntity}"/> per aggregate root, plus the outbox. There is
/// no set for <see cref="DeliveryAttempt"/>: it is inside the message boundary,
/// and exposing it would make it loadable and savable on its own, which is the
/// same as saying it is not inside the boundary after all.
/// <para>
/// Commands and queries share this context and this database. That is CQRS as
/// code organisation rather than as infrastructure, and it is a deliberate
/// stopping point — the separation that pays for itself is separating the
/// <em>models</em>, and separating the stores as well only pays once the read and
/// write loads genuinely diverge.
/// </para>
/// </remarks>
public sealed class RelayDbContext(DbContextOptions<RelayDbContext> options) : DbContext(options)
{
    /// <summary>Messages submitted for delivery.</summary>
    public DbSet<Message> Messages => Set<Message>();

    /// <summary>Domain events waiting to be published.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>Every inbound callback, and what became of it.</summary>
    public DbSet<Callbacks.CallbackRecord> CallbackRecords => Set<Callbacks.CallbackRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Applied by scanning rather than listed one by one. A configuration class
        // that exists but was never registered produces a silently unmapped
        // entity, and the failure surfaces as a confusing runtime error rather
        // than as a missing line anyone would notice in review.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RelayDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every timestamp in this system is an instant, and PostgreSQL's
        // `timestamptz` is the only type that stores one unambiguously. Setting it
        // as a convention rather than per property means a new DateTimeOffset
        // cannot accidentally be mapped to something that drops the offset.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");

        base.ConfigureConventions(configurationBuilder);
    }
}
