namespace Relay.Infrastructure.Persistence.Outbox;

/// <summary>
/// A domain event, durably recorded in the same transaction as the state change
/// that raised it, waiting to be published.
/// </summary>
/// <remarks>
/// This type is infrastructure, not domain. The domain raises
/// <c>IDomainEvent</c> instances and knows nothing about how they travel; this is
/// the row that makes them survive a process dying between the commit and the
/// publish.
/// <para>
/// The failure it removes: without the outbox, publishing happens after
/// <c>SaveChanges</c>, and a crash in that gap loses the event with no trace.
/// Publishing before the commit is worse — subscribers act on a change that then
/// rolls back. The outbox makes the write atomic and the publish retryable, which
/// is at-least-once delivery of events to match at-least-once delivery of
/// messages (ADR 0008).
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    private OutboxMessage(
        Guid id,
        Guid aggregateId,
        string type,
        string payload,
        DateTimeOffset occurredAt)
    {
        Id = id;
        AggregateId = aggregateId;
        Type = type;
        Payload = payload;
        OccurredAt = occurredAt;
    }

    private OutboxMessage()
    {
        Type = null!;
        Payload = null!;
    }

    /// <summary>Identity. Version 7, so inserts append to the index rather than scattering across it.</summary>
    public Guid Id { get; private init; }

    /// <summary>
    /// The aggregate this event came from.
    /// </summary>
    /// <remarks>
    /// A column rather than something read back out of <see cref="Payload"/>.
    /// The payload is <c>jsonb</c>, so a substring match on it is not an operation
    /// PostgreSQL has — the incident question "which events for this message are
    /// still pending" would otherwise need hand-written JSON containment, or a
    /// scan. Indexed, so it is neither.
    /// </remarks>
    public Guid AggregateId { get; private init; }

    /// <summary>
    /// The event's type name, used to deserialize the payload.
    /// </summary>
    /// <remarks>
    /// Stored as the assembly-qualified name without version, so a rebuild does
    /// not orphan rows written by the previous build. Renaming an event type
    /// strands anything already queued, which is why event types are treated as a
    /// published contract rather than as ordinary internal classes.
    /// </remarks>
    public string Type { get; private init; }

    /// <summary>The serialized event.</summary>
    public string Payload { get; private init; }

    /// <summary>When the event happened — not when the row was written.</summary>
    public DateTimeOffset OccurredAt { get; private init; }

    /// <summary>When it was successfully published. Null while it is still pending.</summary>
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>How many publish attempts have been made.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>The last publish failure, kept so a stuck row explains itself.</summary>
    public string? LastError { get; private set; }

    /// <summary>Records an event for publication.</summary>
    public static OutboxMessage For(
        Guid aggregateId,
        string type,
        string payload,
        DateTimeOffset occurredAt) =>
        new(Guid.CreateVersion7(), aggregateId, type, payload, occurredAt);

    /// <summary>Marks the event published.</summary>
    public void MarkProcessed(DateTimeOffset now)
    {
        ProcessedAt = now;
        LastError = null;
    }

    /// <summary>
    /// Records a failed publish attempt.
    /// </summary>
    /// <remarks>
    /// The row stays pending and is retried. There is no dead-letter state here on
    /// purpose: an event that cannot be published is a bug to be fixed and
    /// replayed, not a message to be given up on, and moving it out of the way
    /// would hide that.
    /// </remarks>
    public void RecordFailure(string error)
    {
        AttemptCount++;
        LastError = error;
    }
}
