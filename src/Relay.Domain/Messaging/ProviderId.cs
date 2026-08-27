using System.Text.RegularExpressions;
using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// Identifies a provider implementation.
/// </summary>
/// <remarks>
/// A slug rather than an enum, because the set of providers is not fixed at
/// compile time — that is the entire point of the plugin design (ADR 0003). An
/// enum would put every provider's existence into the core assembly and make
/// adding one an edit to shared code.
/// </remarks>
public sealed partial class ProviderId : ValueObject
{
    private ProviderId(string value) => Value = value;

    /// <summary>The slug: lowercase, dot- or dash-separated, as in <c>email.postal</c>.</summary>
    public string Value { get; }

    /// <summary>Creates a provider id from a slug.</summary>
    public static Result<ProviderId> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MessageErrors.ProviderIdInvalid;
        }

        // Trimmed, but deliberately not lowercased.
        //
        // IdempotencyKey normalises case because callers echo it back inexactly and
        // two spellings genuinely mean one key. A provider id is the opposite: it
        // is written by hand into configuration and routing rules, and it must
        // match a registered provider exactly. Accepting `Email.Postal` for
        // `email.postal` would make a mistyped routing rule appear to work.
        string trimmed = value.Trim();

        return SlugShape().IsMatch(trimmed)
            ? new ProviderId(trimmed)
            : MessageErrors.ProviderIdInvalid;
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex SlugShape();
}
