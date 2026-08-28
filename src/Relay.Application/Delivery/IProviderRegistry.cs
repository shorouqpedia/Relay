using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// What the application knows about a provider without depending on the
/// provider assemblies.
/// </summary>
/// <remarks>
/// A deliberate second description of a provider, alongside the one in
/// <c>Relay.Providers.Abstractions</c>. Application cannot reference that
/// assembly — the whole point of the plugin boundary is that the core does not
/// know which providers exist — so the routing logic is written against this,
/// and the host maps one to the other at registration time.
/// <para>
/// The duplication is real and is the price of the boundary. It is bounded by
/// keeping this to what routing actually needs to decide: who serves this
/// channel, and how long to wait for them.
/// </para>
/// </remarks>
/// <param name="Id">The provider's identifier.</param>
/// <param name="Channel">The channel it serves.</param>
/// <param name="ExpectedReceiptWindow">How long to wait for a delivery receipt before treating silence as an answer.</param>
/// <param name="Priority">
/// Lower is preferred. Providers with equal priority are tried in an
/// unspecified order, so a deliberate preference must be expressed as a
/// difference rather than as position in a list.
/// </param>
public sealed record ProviderProfile(
    ProviderId Id,
    ChannelType Channel,
    TimeSpan ExpectedReceiptWindow,
    int Priority);

/// <summary>
/// The providers this instance discovered at startup.
/// </summary>
/// <remarks>
/// Fixed for the lifetime of the process. Providers are discovered from loaded
/// assemblies, so the set cannot change without a restart, and pretending
/// otherwise would invite a lookup on every message for an answer that never
/// varies.
/// </remarks>
public interface IProviderRegistry
{
    /// <summary>Every registered provider.</summary>
    IReadOnlyList<ProviderProfile> All { get; }

    /// <summary>
    /// Providers that serve a channel, most preferred first.
    /// </summary>
    /// <remarks>
    /// Ordering only. Whether a provider is currently usable is a separate
    /// question with a separate answer, because it changes by the second and this
    /// does not.
    /// </remarks>
    IReadOnlyList<ProviderProfile> For(ChannelType channel);

    /// <summary>Looks up one provider, or <see langword="null"/> if it is not registered.</summary>
    ProviderProfile? Find(ProviderId id);
}
