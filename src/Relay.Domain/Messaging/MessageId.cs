namespace Relay.Domain.Messaging;

/// <summary>
/// The identity of a message.
/// </summary>
/// <remarks>
/// A wrapper around <see cref="Guid"/> rather than a bare Guid so that a message
/// id cannot be passed where a provider id or an attempt id is expected. Those
/// mistakes compile silently when every identifier is the same primitive.
/// <para>
/// Version 7 rather than 4, because these are used as a clustered primary key.
/// Version 4 ids are random, so inserts land in arbitrary index pages and
/// fragment the index; version 7 ids are time-ordered, so inserts append.
/// </para>
/// </remarks>
/// <param name="Value">The underlying identifier.</param>
public readonly record struct MessageId(Guid Value)
{
    /// <summary>Creates a new, time-ordered identifier.</summary>
    public static MessageId New() => new(Guid.CreateVersion7());

    /// <summary>Parses an identifier, returning <see langword="null"/> when the input is not one.</summary>
    public static MessageId? TryParse(string? value) =>
        Guid.TryParse(value, out Guid parsed) ? new MessageId(parsed) : null;

    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}
