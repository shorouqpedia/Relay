using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Relay.Domain.Common;
using Relay.Infrastructure.Persistence.Outbox;

namespace Relay.Infrastructure.Persistence;

/// <summary>
/// Commits state changes and the events they raised as one transaction.
/// </summary>
/// <remarks>
/// The whole design is in the ordering of <see cref="SaveChangesAsync"/>: events
/// are drained from the tracked aggregates and turned into outbox rows
/// <em>before</em> the single <c>SaveChanges</c>, so both land in the same
/// transaction or neither does. There is no window in which a message is recorded
/// as sent and the event announcing it is lost.
/// <para>
/// Publishing happens later, from those rows, by a separate dispatcher. That is
/// what makes event delivery at-least-once: a crash after the commit loses
/// nothing, because the row is still there and still unprocessed.
/// </para>
/// </remarks>
internal sealed class UnitOfWork(RelayDbContext context, TimeProvider clock) : IUnitOfWork
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        WriteOutboxRows();

        try
        {
            return await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsIdempotencyKeyViolation(exception))
        {
            // Checked before the concurrency case, because a unique-index
            // violation is also a DbUpdateException and the more specific reading
            // is the useful one. The submit handler turns this back into an
            // ordinary result; nothing above the persistence layer ever sees a
            // Postgres error code.
            throw new DuplicateIdempotencyKeyException(
                "A message with this idempotency key already exists.",
                exception);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Translated at the boundary of the persistence layer so that nothing
            // above it has to know EF Core exists. The domain declares this
            // failure as its own exception type (see IUnitOfWork), and callers
            // handle that rather than a store-specific one.
            throw new ConcurrencyConflictException(
                "A message was modified by another writer between load and save.",
                exception);
        }
    }

    /// <summary>
    /// Whether a failed write was the idempotency index refusing a duplicate.
    /// </summary>
    /// <remarks>
    /// Matched on the constraint name rather than only on the SQLSTATE, because
    /// <c>23505</c> means "some unique constraint was violated" and this system has
    /// several. Treating any of them as a duplicate submission would silently
    /// return the wrong message to a caller whose insert failed for an unrelated
    /// reason.
    /// </remarks>
    private static bool IsIdempotencyKeyViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: "23505",
            ConstraintName: "ix_messages_idempotency_key",
        };

    private void WriteOutboxRows()
    {
        List<IHasDomainEvents> aggregates = context
            .ChangeTracker
            .Entries<IHasDomainEvents>()
            .Select(static entry => entry.Entity)
            .Where(static aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();

        if (aggregates.Count == 0)
        {
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();

        foreach (IHasDomainEvents aggregate in aggregates)
        {
            Guid aggregateId = aggregate.AggregateId;

            foreach (IDomainEvent domainEvent in aggregate.DrainDomainEvents())
            {
                context.OutboxMessages.Add(ToOutboxRow(aggregateId, domainEvent, now));
            }
        }
    }

    private static OutboxMessage ToOutboxRow(
        Guid aggregateId,
        IDomainEvent domainEvent,
        DateTimeOffset writtenAt)
    {
        Type type = domainEvent.GetType();

        // Assembly-qualified, with the version stripped. The full name orphans
        // every queued row on the next version bump; the bare type name collides
        // as soon as two assemblies define an event with the same name.
        string typeName = $"{type.FullName}, {type.Assembly.GetName().Name}";

        return OutboxMessage.For(
            aggregateId,
            typeName,
            JsonSerializer.Serialize(domainEvent, type, SerializerOptions),

            // The event's own timestamp, not the moment the row was written. Those
            // differ by however long the transaction took, and consumers ordering
            // by this column care when the thing happened.
            domainEvent.OccurredAt is var occurred && occurred != default ? occurred : writtenAt);
    }
}
