namespace Relay.Domain.Common;

/// <summary>
/// The entry point to a consistency boundary. Everything inside an aggregate is
/// reached through its root, and the root is responsible for the invariants that
/// span its contents.
/// </summary>
/// <remarks>
/// Only aggregate roots get repositories. That is the practical consequence of
/// the boundary: if a type can be loaded and saved independently, it is not
/// inside someone else's aggregate.
/// </remarks>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class AggregateRoot<TId> : Entity<TId>, IHasDomainEvents
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <inheritdoc />
    /// <remarks>
    /// Abstract rather than derived from <see cref="Entity{TId}.Id"/>, because
    /// <c>TId</c> is unconstrained and there is no general way to turn one into a
    /// <see cref="Guid"/>. Each aggregate says how its own identity is expressed
    /// as a correlation key, and a new aggregate that forgets to is a compile
    /// error rather than a silently uncorrelatable outbox row.
    /// </remarks>
    public abstract Guid AggregateId { get; }

    /// <inheritdoc />
    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    /// <inheritdoc />
    protected AggregateRoot()
    {
    }

    /// <summary>
    /// Events raised since this aggregate was loaded, in the order they occurred.
    /// </summary>
    /// <remarks>
    /// The aggregate accumulates events rather than dispatching them, so that
    /// nothing observes a state change until the transaction that produced it
    /// commits. The unit of work drains this collection at save time.
    /// </remarks>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>
    /// Optimistic concurrency token, maintained by the persistence layer.
    /// </summary>
    /// <remarks>
    /// Two workers can pick up the same message; the row version is what makes the
    /// second one lose rather than silently overwrite the first. Delivery state is
    /// exactly the kind of data where a lost update means a duplicate send.
    /// </remarks>
    public uint Version { get; protected set; }

    /// <summary>Removes and returns the accumulated events.</summary>
    public IReadOnlyCollection<IDomainEvent> DrainDomainEvents()
    {
        IDomainEvent[] drained = [.. _domainEvents];
        _domainEvents.Clear();
        return drained;
    }

    /// <summary>Records that something happened. Called from within a state transition.</summary>
    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
