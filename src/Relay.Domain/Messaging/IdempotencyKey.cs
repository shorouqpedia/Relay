using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// The caller-supplied token that makes a submission safe to repeat.
/// </summary>
/// <remarks>
/// This type exists to make one property true everywhere: two keys that a human
/// would call "the same key" compare equal. Callers retrying a request do not
/// reliably reproduce the original casing or surrounding whitespace, and a
/// duplicate that slips through because of a stray space is indistinguishable
/// from a genuine second message.
/// <para>
/// Normalisation therefore happens once, at construction, and the normalised form
/// is the only form that exists. Nothing downstream — the unique index, the cache
/// lookup, the equality check — has to remember to normalise, because there is no
/// un-normalised value to forget about.
/// </para>
/// See ADR 0008 for why duplicate suppression rests on a database constraint
/// rather than a lookup.
/// </remarks>
public sealed class IdempotencyKey : ValueObject
{
    /// <summary>Shortest accepted key. Below this, collisions across callers stop being unlikely.</summary>
    public const int MinLength = 8;

    /// <summary>Longest accepted key. Matches the unique index width.</summary>
    public const int MaxLength = 128;

    private IdempotencyKey(string value) => Value = value;

    /// <summary>The normalised key: trimmed and lowercased.</summary>
    public string Value { get; }

    /// <summary>Creates a key, normalising it and rejecting one that cannot serve its purpose.</summary>
    public static Result<IdempotencyKey> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MessageErrors.IdempotencyKeyInvalid;
        }

        string normalised = value.Trim().ToLowerInvariant();

        return normalised.Length is < MinLength or > MaxLength
            ? MessageErrors.IdempotencyKeyInvalid
            : new IdempotencyKey(normalised);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
