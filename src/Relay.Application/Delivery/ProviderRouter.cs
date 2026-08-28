using Microsoft.Extensions.Logging;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// Chooses which provider should carry a message.
/// </summary>
/// <remarks>
/// The selection is: providers serving the message's channel, in priority order,
/// minus the ones currently unavailable, minus the ones this message has already
/// failed on.
/// <para>
/// That last exclusion is the one worth reading twice. Without it, a message that
/// fails on its preferred provider is handed straight back to the same provider
/// on the retry, because priority order has not changed — so the message burns
/// its entire budget on one upstream while a working alternative sits idle. The
/// retry budget is only a failover budget if each attempt goes somewhere new.
/// </para>
/// </remarks>
public sealed class ProviderRouter(
    IProviderRegistry registry,
    IProviderHealth health,
    ILogger<ProviderRouter> logger)
{
    /// <summary>
    /// Picks a provider for a message, or explains why none is available.
    /// </summary>
    public Result<ProviderProfile> Route(Message message)
    {
        IReadOnlyList<ProviderProfile> candidates = registry.For(message.Channel);

        if (candidates.Count == 0)
        {
            // No provider serves this channel at all. A configuration fault
            // rather than an outage, and it will not resolve by retrying.
            logger.LogError(
                "No provider is registered for the {Channel} channel. "
                + "Messages on this channel cannot be delivered by this instance.",
                message.Channel);

            return MessageErrors.NoEligibleProvider;
        }

        HashSet<ProviderId> alreadyTried = [.. message.Attempts.Select(a => a.ProviderId)];

        ProviderProfile? chosen = candidates
            .FirstOrDefault(p => !alreadyTried.Contains(p.Id) && health.IsAvailable(p.Id));

        if (chosen is not null)
        {
            return chosen;
        }

        // Every untried provider is unavailable, or there are no untried ones
        // left. Falling back to a provider this message already failed on is
        // better than not sending it: the earlier failure may have been transient,
        // and refusing to retry would turn a recoverable failure into a permanent
        // one. It only happens once the alternatives are exhausted.
        ProviderProfile? fallback = candidates.FirstOrDefault(p => health.IsAvailable(p.Id));

        if (fallback is not null)
        {
            logger.LogWarning(
                "Message {MessageId} has tried every available provider for {Channel}. "
                + "Falling back to {ProviderId}, which it has already failed on.",
                message.Id,
                message.Channel,
                fallback.Id);

            return fallback;
        }

        logger.LogWarning(
            "No provider for {Channel} is currently available. "
            + "Message {MessageId} stays pending.",
            message.Channel,
            message.Id);

        return MessageErrors.NoEligibleProvider;
    }
}
