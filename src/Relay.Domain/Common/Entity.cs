namespace Relay.Domain.Common;

/// <summary>
/// Something with an identity that persists across changes to its state.
/// </summary>
/// <remarks>
/// Two entities are the same entity when their identifiers match, regardless of
/// whether any other value differs. This is the opposite of the rule for a
/// <see cref="ValueObject"/>, and it is the whole distinction between the two.
/// </remarks>
/// <typeparam name="TId">The identifier type.</typeparam>
public abstract class Entity<TId> : IEquatable<Entity<TId>>
    where TId : notnull
{
    /// <summary>Initialises the entity with its identity.</summary>
    protected Entity(TId id) => Id = id;

    /// <summary>
    /// Required by EF Core, which materialises entities without going through a
    /// domain constructor. Not for use from domain code.
    /// </summary>
    protected Entity() => Id = default!;

    /// <summary>The identity of this entity.</summary>
    public TId Id { get; protected init; }

    /// <inheritdoc />
    public bool Equals(Entity<TId>? other) =>
        other is not null && other.GetType() == GetType() && other.Id.Equals(Id);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Entity<TId> entity && Equals(entity);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    /// <summary>Compares two entities by identity.</summary>
    public static bool operator ==(Entity<TId>? left, Entity<TId>? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Compares two entities by identity.</summary>
    public static bool operator !=(Entity<TId>? left, Entity<TId>? right) => !(left == right);
}
