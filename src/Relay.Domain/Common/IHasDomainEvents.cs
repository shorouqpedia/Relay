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
    /// <summary>Events raised since the aggregate was loaded.</summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    /// <summary>Removes and returns the accumulated events.</summary>
    IReadOnlyCollection<IDomainEvent> DrainDomainEvents();
}
