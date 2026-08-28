using Microsoft.Extensions.Options;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// The providers this instance discovered, described in the application's terms.
/// </summary>
/// <remarks>
/// This class is the seam between the two halves of the plugin design. The
/// provider assemblies describe themselves with
/// <see cref="ProviderDescriptor"/>; the application routes using
/// <see cref="ProviderProfile"/>; neither references the other's assembly, and
/// this translates once at startup.
/// </remarks>
internal sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, ProviderProfile> _byId;
    private readonly Dictionary<ChannelType, ProviderProfile[]> _byChannel;

    public ProviderRegistry(
        IEnumerable<IMessageProvider> providers,
        IOptions<RoutingOptions> routing)
    {
        RoutingOptions options = routing.Value;

        ProviderProfile[] profiles = [.. providers
            .Select(p => new ProviderProfile(
                p.Descriptor.Id,
                p.Descriptor.Channel,
                p.Descriptor.ExpectedReceiptWindow,
                options.PriorityFor(p.Descriptor.Id)))];

        _byId = profiles.ToDictionary(p => p.Id.Value, StringComparer.Ordinal);

        // Ordered once, here, rather than on every routing decision. The set is
        // fixed for the lifetime of the process, so sorting per message would be
        // recomputing an answer that cannot change.
        _byChannel = profiles
            .GroupBy(p => p.Channel)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(p => p.Priority).ThenBy(p => p.Id.Value, StringComparer.Ordinal).ToArray());

        All = profiles;
    }

    public IReadOnlyList<ProviderProfile> All { get; }

    public IReadOnlyList<ProviderProfile> For(ChannelType channel) =>
        _byChannel.TryGetValue(channel, out ProviderProfile[]? providers) ? providers : [];

    public ProviderProfile? Find(ProviderId id) =>
        _byId.TryGetValue(id.Value, out ProviderProfile? profile) ? profile : null;
}

/// <summary>
/// Which provider to prefer, per channel.
/// </summary>
/// <remarks>
/// Preference is configuration rather than code, because it is an operational
/// decision — a contract renegotiation, a provider having a bad week — and
/// operational decisions should not need a deployment.
/// </remarks>
public sealed class RoutingOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Delivery:Routing";

    /// <summary>
    /// Priority per provider id. Lower is preferred.
    /// </summary>
    /// <remarks>
    /// A provider absent from this map gets <see cref="DefaultPriority"/> rather
    /// than being excluded. Excluding by omission would mean a newly added
    /// provider is silently never used until someone remembers to configure it,
    /// which is the opposite of what the zero-edit design is for.
    /// </remarks>
    public IReadOnlyDictionary<string, int> Priorities { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Priority given to a provider not named in <see cref="Priorities"/>.</summary>
    public int DefaultPriority { get; init; } = 100;

    /// <summary>Resolves a provider's priority.</summary>
    public int PriorityFor(ProviderId id) =>
        Priorities.TryGetValue(id.Value, out int priority) ? priority : DefaultPriority;
}
