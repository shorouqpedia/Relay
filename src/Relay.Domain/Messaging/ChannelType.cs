namespace Relay.Domain.Messaging;

/// <summary>
/// How a message physically reaches its recipient.
/// </summary>
/// <remarks>
/// A channel is not a provider. The channel is what the caller asks for — "send
/// this as an SMS" — and the provider is who Relay picks to do it. Several
/// providers serve one channel, and that is what makes failover possible.
/// </remarks>
public enum ChannelType
{
    /// <summary>Unset. Present so that a default-initialised value is invalid rather than meaningful.</summary>
    None = 0,

    /// <summary>Electronic mail.</summary>
    Email = 1,

    /// <summary>Short message service.</summary>
    Sms = 2,

    /// <summary>Push notification to a registered device.</summary>
    Push = 3,

    /// <summary>An HTTP callback to a recipient-supplied endpoint.</summary>
    Webhook = 4,
}
