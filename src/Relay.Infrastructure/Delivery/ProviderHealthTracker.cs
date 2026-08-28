using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Tracks whether each provider is currently worth trying.
/// </summary>
/// <remarks>
/// A circuit breaker at the <em>routing</em> level, which is a different thing
/// from the one in the decorator chain. The chain's breaker stops a single call
/// from hammering a failing upstream; this one stops the router from choosing
/// that upstream in the first place, so a message fails over to a working
/// provider instead of spending an attempt discovering what the system already
/// knows.
/// <para>
/// In-memory, and therefore per-instance. Several workers each learn about an
/// outage separately, and each pays a few failed attempts to do so. Sharing the
/// state through Redis would remove that duplication and add a dependency on
/// Redis being up in order to route at all — which turns a cache outage into a
/// delivery outage. Per-instance state degrades to "slightly more failed
/// attempts"; shared state degrades to "nothing is delivered".
/// </para>
/// </remarks>
public sealed class ProviderHealthTracker(
    IOptions<ProviderHealthOptions> options,
    TimeProvider clock,
    ILogger<ProviderHealthTracker> logger) : IProviderHealth
{
    private readonly ConcurrentDictionary<string, ProviderState> _states = new(StringComparer.Ordinal);
    private readonly ProviderHealthOptions _options = options.Value;

    public bool IsAvailable(ProviderId provider)
    {
        if (!_states.TryGetValue(provider.Value, out ProviderState? state))
        {
            // Never seen. Optimism is correct here: a provider is presumed
            // working until it demonstrates otherwise, and the alternative would
            // require a health check before the first message could ever be sent.
            return true;
        }

        return state.BlockedUntil is not { } until || clock.GetUtcNow() >= until;
    }

    public void Record(ProviderId provider, AttemptOutcome outcome, TimeSpan? retryAfter = null)
    {
        ProviderState state = _states.GetOrAdd(provider.Value, static _ => new ProviderState());

        lock (state.Gate)
        {
            switch (outcome)
            {
                case AttemptOutcome.Accepted:
                case AttemptOutcome.Rejected:
                    // Rejected counts as healthy, and that is deliberate. The
                    // provider answered correctly and promptly; it simply refused
                    // this recipient. Treating a bad address as evidence of an
                    // outage would break a working provider on a bad address list.
                    Reset(provider, state);
                    break;

                case AttemptOutcome.RateLimited:
                    // Honour what the provider asked for. Backing off less than
                    // requested escalates a refusal into a blocked account.
                    Block(provider, state, retryAfter ?? _options.RateLimitBackoff, outcome);
                    break;

                case AttemptOutcome.TransientFailure:
                case AttemptOutcome.Timeout:
                    state.ConsecutiveFailures++;

                    if (state.ConsecutiveFailures >= _options.FailuresBeforeBreak)
                    {
                        Block(provider, state, _options.BreakDuration, outcome);
                    }

                    break;

                case AttemptOutcome.None:
                default:
                    break;
            }
        }
    }

    private void Reset(ProviderId provider, ProviderState state)
    {
        bool wasBlocked = state.BlockedUntil is not null;

        state.ConsecutiveFailures = 0;
        state.BlockedUntil = null;

        if (wasBlocked)
        {
            logger.LogInformation("Provider {ProviderId} is available again.", provider.Value);
        }
    }

    private void Block(ProviderId provider, ProviderState state, TimeSpan duration, AttemptOutcome cause)
    {
        state.BlockedUntil = clock.GetUtcNow().Add(duration);

        logger.LogWarning(
            "Provider {ProviderId} is out of rotation for {Seconds}s after {Outcome}.",
            provider.Value,
            duration.TotalSeconds,
            cause);
    }

    private sealed class ProviderState
    {
        public Lock Gate { get; } = new();

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? BlockedUntil { get; set; }
    }
}

/// <summary>Thresholds for taking a provider out of rotation.</summary>
public sealed class ProviderHealthOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Delivery:ProviderHealth";

    /// <summary>
    /// Consecutive transient failures before a provider stops being offered work.
    /// </summary>
    /// <remarks>
    /// Not one. A single timeout is ordinary and says nothing; taking a provider
    /// out of rotation on it would make routing oscillate under normal noise.
    /// </remarks>
    public int FailuresBeforeBreak { get; init; } = 5;

    /// <summary>How long a provider stays out of rotation after breaking.</summary>
    public TimeSpan BreakDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Fallback backoff when a provider rate-limits without saying for how long.</summary>
    public TimeSpan RateLimitBackoff { get; init; } = TimeSpan.FromSeconds(60);
}
