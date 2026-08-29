using Microsoft.Extensions.Diagnostics.HealthChecks;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Observability;

/// <summary>
/// Reports whether each channel still has a provider that can carry it.
/// </summary>
/// <remarks>
/// Not a check that every provider is up. A system with four email providers and
/// one of them broken is working exactly as designed — reporting that as
/// unhealthy would page someone for the failover doing its job, and worse, would
/// train them to ignore the page.
/// <para>
/// What matters is whether any channel has <em>nothing left</em>. That is the
/// condition where messages stop moving, and it is the only one worth waking
/// somebody for.
/// </para>
/// <para>
/// Reported as <see cref="HealthStatus.Degraded"/> rather than
/// <see cref="HealthStatus.Unhealthy"/>, deliberately. Unhealthy is what an
/// orchestrator restarts or removes from a load balancer, and neither helps here:
/// the process is fine, the upstream is not, and taking the instance out of
/// rotation would only stop it accepting the messages it can still queue.
/// </para>
/// </remarks>
public sealed class ProviderHealthCheck(IProviderRegistry registry, IProviderHealth health)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> data = [];
        List<string> starved = [];

        foreach (IGrouping<ChannelType, ProviderProfile> channel in
                 registry.All.GroupBy(p => p.Channel))
        {
            int available = channel.Count(p => health.IsAvailable(p.Id));

            data[channel.Key.ToString()] = $"{available}/{channel.Count()} available";

            if (available == 0)
            {
                starved.Add(channel.Key.ToString());
            }
        }

        if (registry.All.Count == 0)
        {
            // No providers at all is a configuration fault, not an outage — and it
            // is the exact condition that went unnoticed for three milestones when
            // discovery was silently finding nothing. Worth reporting loudly.
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "No providers are registered. Nothing can be delivered.",
                data: data));
        }

        return Task.FromResult(starved.Count == 0
            ? HealthCheckResult.Healthy("Every channel has an available provider.", data)
            : HealthCheckResult.Degraded(
                $"No provider is currently available for: {string.Join(", ", starved)}.",
                data: data));
    }
}
