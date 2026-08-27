using System.Text.RegularExpressions;
using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// Where a message is going, in a form the channel can actually use.
/// </summary>
/// <remarks>
/// A recipient is only meaningful in the context of a channel: <c>+201234567890</c>
/// is a valid destination for SMS and nonsense for email. Binding the two together
/// in one type means "a valid recipient" is a complete statement, and a message
/// cannot hold an address that its own channel cannot reach.
/// <para>
/// Validation here is deliberately shallow — shape, not existence. Whether an
/// address is deliverable is something only the provider can answer, and finding
/// out is what a delivery attempt is for.
/// </para>
/// </remarks>
public sealed partial class Recipient : ValueObject
{
    private Recipient(ChannelType channel, string address)
    {
        Channel = channel;
        Address = address;
    }

    /// <summary>The channel this address is valid for.</summary>
    public ChannelType Channel { get; }

    /// <summary>The normalised destination address.</summary>
    public string Address { get; }

    /// <summary>Creates a recipient, rejecting an address the channel cannot use.</summary>
    public static Result<Recipient> Create(ChannelType channel, string? address)
    {
        if (channel is ChannelType.None)
        {
            return MessageErrors.ChannelMissing;
        }

        if (string.IsNullOrWhiteSpace(address))
        {
            return MessageErrors.RecipientMissing;
        }

        string trimmed = address.Trim();

        return IsValidForChannel(channel, trimmed)
            ? new Recipient(channel, Normalise(channel, trimmed))
            : MessageErrors.RecipientNotValidForChannel(channel);
    }

    private static bool IsValidForChannel(ChannelType channel, string address) => channel switch
    {
        ChannelType.Email => EmailShape().IsMatch(address),
        ChannelType.Sms => E164Shape().IsMatch(address),
        ChannelType.Push => address.Length is >= 16 and <= 512,
        ChannelType.Webhook => IsHttpsUrl(address),
        _ => false,
    };

    private static string Normalise(ChannelType channel, string address) => channel switch
    {
        // Only the domain half of an address is case-insensitive, but no provider
        // in practice treats the local part as case-sensitive, and lowercasing the
        // whole thing is what makes two spellings of one address deduplicate.
        ChannelType.Email => address.ToLowerInvariant(),
        _ => address,
    };

    private static bool IsHttpsUrl(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps;

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Channel;
        yield return Address;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Channel}:{Address}";

    // Deliberately not RFC 5322. A fully conformant email pattern accepts
    // addresses no provider will deliver to and is unreadable besides; this
    // rejects the typos worth rejecting and defers the rest to the provider.
    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex EmailShape();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex E164Shape();
}
