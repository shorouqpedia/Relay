namespace Relay.Domain.Common;

/// <summary>
/// Something defined entirely by its values, with no identity of its own.
/// </summary>
/// <remarks>
/// Two value objects with equal components are interchangeable — there is no
/// sense in which one is "the same one" as another, because there is nothing to
/// be the same as. Value objects are immutable for the same reason: changing one
/// would make it a different value, not a changed thing.
/// <para>
/// This exists as a base class rather than a record because the domain's value
/// objects are constructed through validating factories that return a
/// <see cref="Result{TValue}"/>. A record's positional constructor cannot fail,
/// so it can only express types where every combination of components is valid.
/// </para>
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// The components that define equality for this value, in a stable order.
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public bool Equals(ValueObject? other) =>
        other is not null
        && other.GetType() == GetType()
        && other.GetEqualityComponents().SequenceEqual(GetEqualityComponents());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject value && Equals(value);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (object? component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>Compares two values component by component.</summary>
    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>Compares two values component by component.</summary>
    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
