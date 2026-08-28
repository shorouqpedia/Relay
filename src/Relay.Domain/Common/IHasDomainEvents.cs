namespace Relay.Domain.Common;

/// <summary>
/// Something that accumulates domain events for the unit of work to drain.
/// </summary>
/// <remarks>
/// Exists so that the outbox does not have to name a specific aggregate or a
/// specific identifier type. Without it, the code that drains events has to be
/// written against <c>AggregateRoot&lt;TId&gt;</c> for one concrete
/// <c>TId</c> — which works exactly as long as there is one aggregate, and stops
/// silently the moment a second one is added with a different key type. Events
/// from the new aggregate would simply never be collected, and nothing would fail.
/// </remarks>
public interface IHasDomainEvents
{
    /// <summary>
    /// This aggregate's identity, as a correlation key.
    /// </summary>
    /// <remarks>
    /// Implemented explicitly by each aggregate rather than derived from
    /// <c>Entity&lt;TId&gt;</c>, because identifier types differ and only the
    /// aggregate knows how to express its own as a plain <see cref="Guid"/>.
    /// <para>
    /// It exists so the outbox can record which aggregate an event came from in a
    /// column of its own. The alternative — reading it back out of the serialized
    /// payload — turned out not to work: the payload is <c>jsonb</c>, so a
    /// substring match on it is not an operation PostgreSQL has, and answering
    /// "which events for this message are still pending" would have needed either
    /// JSON containment written by hand or a full scan.
    /// </para>
    /// </remarks>
    Guid AggregateId { get; }

    /// <summary>Events raised since the aggregate was loaded.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Removes and returns the accumulated events.</summary>
    IReadOnlyCollection<IDomainEvent> DrainDomainEvents();
}
