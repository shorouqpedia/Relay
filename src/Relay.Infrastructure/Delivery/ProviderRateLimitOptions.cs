using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Delivery.Decorators;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Per-provider quotas and retry budgets.
/// </summary>
/// <remarks>
/// Both are configuration rather than code because both are facts about someone
/// else's system — a contracted rate, a provider having a bad week — and neither
/// should need a deployment to change.
/// </remarks>
public sealed class ProviderRateLimitOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Delivery:RateLimits";

    private readonly ConcurrentDictionary<string, RateLimiter> _limiters =
        new(StringComparer.Ordinal);

    /// <summary>Quota per provider id.</summary>
    public IReadOnlyDictionary<string, ProviderQuota> Providers { get; init; } =
        new Dictionary<string, ProviderQuota>(StringComparer.Ordinal);

    /// <summary>
    /// Applied to a provider with no configured quota.
    /// </summary>
    /// <remarks>
    /// Generous rather than restrictive. A newly added provider must not be
    /// throttled to a crawl because nobody remembered to configure it — that would
    /// make the zero-edit design (ADR 0003) true in the build and false in
    /// practice.
    /// </remarks>
    public ProviderQuota Default { get; init; } = new();

    /// <summary>
    /// The limiter for a provider, created once and shared.
    /// </summary>
    /// <remarks>
    /// Shared per provider id, because a quota is a property of the upstream, not
    /// of a caller. A limiter per scope would mean each worker independently
    /// permitted the full rate, and the aggregate would exceed it by however many
    /// workers are running.
    /// </remarks>
    public RateLimiter LimiterFor(ProviderId provider) =>
        _limiters.GetOrAdd(provider.Value, id => Build(QuotaFor(id)));

    /// <summary>The retry budget for a provider.</summary>
    public ResilienceSettings ResilienceFor(ProviderId provider) => QuotaFor(provider.Value).Resilience;

    private ProviderQuota QuotaFor(string id) =>
        Providers.TryGetValue(id, out ProviderQuota? quota) ? quota : Default;

    private static SlidingWindowRateLimiter Build(ProviderQuota quota) =>
        new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = quota.PermitsPerWindow,
            Window = quota.Window,

            // Segments make the window slide rather than reset. A fixed window
            // lets a caller spend the whole quota in its last instant and the
            // whole next quota in the first — twice the contracted rate across
            // the boundary, which is exactly when a provider notices.
            SegmentsPerWindow = quota.Segments,

            QueueLimit = quota.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
}

/// <summary>What one provider will tolerate.</summary>
public sealed class ProviderQuota
{
    /// <summary>Calls permitted per <see cref="Window"/>.</summary>
    public int PermitsPerWindow { get; init; } = 100;

    /// <summary>The window the permits are counted over.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How finely the window slides.</summary>
    public int Segments { get; init; } = 10;

    /// <summary>
    /// How many calls may wait for a permit before the limiter refuses.
    /// </summary>
    /// <remarks>
    /// Deliberately small. A long queue converts a rate limit into latency, which
    /// holds worker slots and hides the problem; a short one converts it into a
    /// visible <see cref="AttemptOutcome.RateLimited"/> that fails the message
    /// over to another provider. Failing over beats waiting.
    /// </remarks>
    public int QueueLimit { get; init; } = 4;

    /// <summary>The retry budget for one logical attempt on this provider.</summary>
    public ResilienceSettings Resilience { get; init; } = new();
}
