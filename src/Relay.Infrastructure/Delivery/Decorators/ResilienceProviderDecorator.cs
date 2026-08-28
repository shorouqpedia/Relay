using Microsoft.Extensions.Logging;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Infrastructure.Delivery.Decorators;

/// <summary>
/// Retries a provider call when, and only when, retrying could change the answer.
/// </summary>
/// <remarks>
/// Innermost in the chain (ADR 0007), adjacent to the real call, so it sees the
/// raw outcome before any other layer has interpreted it.
/// <para>
/// The retry predicate is the whole substance of this class, and it is
/// domain-shaped rather than transport-shaped:
/// </para>
/// <list type="bullet">
/// <item><see cref="AttemptOutcome.TransientFailure"/> — retried. Something broke
/// that may not break again.</item>
/// <item><see cref="AttemptOutcome.Rejected"/> — never retried. The provider
/// understood and said no; asking again asks the same question.</item>
/// <item><see cref="AttemptOutcome.RateLimited"/> — never retried <em>here</em>.
/// Retrying inside one logical attempt would spend the whole backoff holding a
/// worker slot, and the provider has already said it wants less traffic. It goes
/// back to the caller, which fails the message over to another provider.</item>
/// <item><see cref="AttemptOutcome.Timeout"/> — never retried. The message may
/// already have been sent, so a retry here risks a duplicate <em>within</em> what
/// the domain records as a single attempt, and the attempt history would no
/// longer describe what actually happened. Retrying a timeout is a decision for
/// the domain to make and record (ADR 0008), not one to make invisibly.</item>
/// </list>
/// <para>
/// That leaves one retryable outcome, which looks thin until you notice it is the
/// only one where retrying is both safe and potentially useful. A resilience layer
/// that retried everything would produce duplicates and quota breaches while
/// appearing more robust.
/// </para>
/// <para>
/// Written by hand rather than over a resilience library. The interesting part is
/// the predicate above, not the backoff arithmetic, and expressing a
/// four-way outcome rule as library predicates would bury it. There is also
/// nothing HTTP here to hang an HTTP resilience handler on — that layer still
/// exists, inside each provider's typed client, bounding a single physical call.
/// See the amendment on ADR 0007.
/// </para>
/// </remarks>
internal sealed class ResilienceProviderDecorator(
    IMessageProvider inner,
    ResilienceSettings settings,
    TimeProvider clock,
    ILogger<ResilienceProviderDecorator> logger) : IMessageProvider
{
    public ProviderDescriptor Descriptor => inner.Descriptor;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        DeliveryResult result = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);

        for (int retry = 1; retry <= settings.MaxRetries; retry++)
        {
            if (result.Outcome is not AttemptOutcome.TransientFailure)
            {
                return result;
            }

            TimeSpan delay = BackoffFor(retry);

            logger.LogDebug(
                "Retrying {ProviderId} for message {MessageId} in {Delay}ms (retry {Retry} of {Max}).",
                Descriptor.Id.Value,
                request.MessageId.Value,
                delay.TotalMilliseconds,
                retry,
                settings.MaxRetries);

            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);

            result = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Exponential backoff with full jitter.
    /// </summary>
    /// <remarks>
    /// Jittered because the alternative synchronises the retries. Several workers
    /// hitting one provider outage all fail at the same moment, and with a fixed
    /// backoff they all retry at the same moment too — turning a recovering
    /// provider's first good second into a thundering herd. Full jitter spreads
    /// them across the whole window.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5394:Do not use insecure randomness",
        Justification =
            "The randomness here spreads retry timing across workers. It is not a "
            + "secret, is not used to derive one, and predicting it gains an attacker "
            + "nothing — the worst outcome is that retries bunch up, which is the "
            + "behaviour with no jitter at all. A cryptographic generator would cost "
            + "entropy on a hot path to defend against nothing.")]
    private TimeSpan BackoffFor(int retry)
    {
        double exponential = settings.BaseDelay.TotalMilliseconds * Math.Pow(2, retry - 1);
        double capped = Math.Min(exponential, settings.MaxDelay.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * capped);
    }
}

/// <summary>How hard to retry a provider within one logical attempt.</summary>
/// <remarks>
/// Kept small. This budget multiplies with the message's own retry budget — three
/// retries here and three attempts in the domain is nine physical calls — and the
/// domain's budget is the one that can fail over to a different provider, so it is
/// the one worth spending.
/// </remarks>
public sealed class ResilienceSettings
{
    /// <summary>Retries after the first call. Zero disables retrying.</summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>The first backoff, doubled on each subsequent retry.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Ceiling on the backoff, before jitter.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(5);
}
