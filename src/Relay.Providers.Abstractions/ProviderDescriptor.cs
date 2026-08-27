using Relay.Domain.Messaging;

namespace Relay.Providers.Abstractions;

/// <summary>
/// What a provider can do, declared rather than discovered.
/// </summary>
/// <remarks>
/// The router reads these to decide what it can ask of a provider. Discovering a
/// missing capability by calling and failing would burn a delivery attempt to
/// learn something the provider knew all along.
/// </remarks>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>Nothing beyond sending.</summary>
    None = 0,

    /// <summary>
    /// Returns an identifier for an accepted message.
    /// </summary>
    /// <remarks>
    /// Without this, an inbound receipt cannot be matched to a message, so the
    /// provider is unreconcilable no matter what else it supports.
    /// </remarks>
    ReturnsMessageId = 1 << 0,

    /// <summary>Sends delivery receipts to a callback endpoint.</summary>
    PushesDeliveryReceipts = 1 << 1,

    /// <summary>Can be asked the current status of a message it accepted.</summary>
    /// <remarks>See <see cref="IReceiptQueryable"/>. This is what lets the sweeper
    /// resolve a message rather than only give up on it.</remarks>
    QueryableReceipts = 1 << 2,

    /// <summary>Honours an idempotency key of its own, suppressing duplicate sends upstream.</summary>
    /// <remarks>
    /// An optimisation only. Relay's own guarantee never depends on it — see ADR 0008.
    /// </remarks>
    NativeIdempotency = 1 << 3,

    /// <summary>Reports a retry delay when it refuses for quota reasons.</summary>
    ReportsRetryAfter = 1 << 4,
}

/// <summary>
/// A provider's identity and declared abilities.
/// </summary>
/// <param name="Id">The provider's stable identifier.</param>
/// <param name="Channel">The channel it serves.</param>
/// <param name="Capabilities">What it can do beyond sending.</param>
/// <param name="ExpectedReceiptWindow">
/// How long to wait for a delivery receipt before treating the silence as an
/// answer. Providers differ by orders of magnitude here — an SMS receipt arrives
/// in seconds, an email bounce can take hours — so a single global timeout would
/// either abandon messages that were fine or wait forever on ones that were not.
/// </param>
public sealed record ProviderDescriptor(
    ProviderId Id,
    ChannelType Channel,
    ProviderCapabilities Capabilities,
    TimeSpan ExpectedReceiptWindow)
{
    /// <summary>Whether the provider declares a capability.</summary>
    public bool Supports(ProviderCapabilities capability) =>
        (Capabilities & capability) == capability;
}
