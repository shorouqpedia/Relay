using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Infrastructure.Delivery.Decorators;

/// <summary>
/// Logs one line per logical delivery attempt.
/// </summary>
/// <remarks>
/// Outermost in the chain (ADR 0007). Inside the resilience decorator this would
/// emit a line per physical retry, and "attempts" in the logs would stop matching
/// "attempts" in the domain — so a message that the database says was tried three
/// times would appear nine times in the log, and nobody reading it could tell
/// which number was real.
/// </remarks>
internal sealed class LoggingProviderDecorator(
    IMessageProvider inner,
    ILogger<LoggingProviderDecorator> logger) : IMessageProvider
{
    public ProviderDescriptor Descriptor => inner.Descriptor;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        DeliveryResult result = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.Outcome is AttemptOutcome.Accepted)
        {
            logger.LogInformation(
                "{ProviderId} accepted message {MessageId} as {ProviderMessageId}.",
                Descriptor.Id.Value,
                request.MessageId.Value,
                result.ProviderMessageId);
        }
        else
        {
            logger.LogWarning(
                "{ProviderId} returned {Outcome} for message {MessageId}: {Reason}",
                Descriptor.Id.Value,
                result.Outcome,
                request.MessageId.Value,
                result.FailureReason);
        }

        return result;
    }
}

/// <summary>
/// Records what the caller experienced.
/// </summary>
/// <remarks>
/// Above the resilience decorator, so the latency histogram measures the whole
/// logical attempt including retry and backoff time. That is the number that
/// governs queue depth and worker occupancy — the per-try latency is a different
/// question, and one the resilience layer answers separately.
/// </remarks>
internal sealed class MetricsProviderDecorator(IMessageProvider inner, DeliveryMetrics metrics)
    : IMessageProvider
{
    public ProviderDescriptor Descriptor => inner.Descriptor;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        try
        {
            DeliveryResult result = await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);

            metrics.RecordAttempt(
                Descriptor.Id,
                Descriptor.Channel,
                result.Outcome,
                Stopwatch.GetElapsedTime(start));

            return result;
        }
        catch (OperationCanceledException)
        {
            // Not recorded as an outcome. A cancelled attempt says nothing about
            // the provider, and counting shutdown as a failure would make every
            // deployment look like an outage in the dashboards.
            throw;
        }
    }
}

/// <summary>
/// Holds calls to a provider's published quota.
/// </summary>
/// <remarks>
/// Above the resilience decorator, and this is the ordering that is easiest to get
/// backwards. Rate limiting has to gate every <em>physical</em> call, and the
/// resilience decorator is the thing that turns one logical call into several. Put
/// the limiter underneath it and retries bypass the quota entirely — so a
/// provider that is refusing because it is overloaded gets hit harder for
/// refusing, which is how a temporary 429 becomes a blocked account.
/// </remarks>
internal sealed class RateLimitingProviderDecorator(
    IMessageProvider inner,
    RateLimiter limiter,
    ILogger<RateLimitingProviderDecorator> logger) : IMessageProvider
{
    public ProviderDescriptor Descriptor => inner.Descriptor;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        using RateLimitLease lease = await limiter
            .AcquireAsync(permitCount: 1, cancellationToken)
            .ConfigureAwait(false);

        if (lease.IsAcquired)
        {
            return await inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Reported as if the provider had rate-limited us, because from every
        // caller's point of view it is the same event and deserves the same
        // handling — back off, and try a different provider. Inventing a separate
        // outcome for "we stopped ourselves" would mean every consumer of the
        // result had to learn to treat two things identically.
        TimeSpan? retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan delay)
            ? delay
            : null;

        logger.LogWarning(
            "Held back a call to {ProviderId} for message {MessageId}: local quota reached.",
            Descriptor.Id.Value,
            request.MessageId.Value);

        return DeliveryResult.RateLimited(
            $"Relay's local quota for {Descriptor.Id} is exhausted.",
            retryAfter);
    }
}

/// <summary>
/// Counters and histograms for delivery.
/// </summary>
/// <remarks>
/// One meter for the whole system, with the provider and channel as tags rather
/// than as separate instrument names. Tags can be aggregated away; instrument
/// names cannot, so a metric per provider would make "how is delivery doing
/// overall" a question the dashboard could not answer.
/// </remarks>
public sealed class DeliveryMetrics : IDisposable
{
    /// <summary>The meter name, for OpenTelemetry registration.</summary>
    public const string MeterName = "Relay.Delivery";

    private readonly Meter _meter;
    private readonly Counter<long> _attempts;
    private readonly Histogram<double> _duration;

    /// <summary>Creates the meter and its instruments.</summary>
    public DeliveryMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _attempts = _meter.CreateCounter<long>(
            "relay.delivery.attempts",
            unit: "{attempt}",
            description: "Delivery attempts, tagged by provider, channel, and outcome.");

        _duration = _meter.CreateHistogram<double>(
            "relay.delivery.duration",
            unit: "s",
            description: "How long a logical delivery attempt took, including retries.");
    }

    /// <summary>Records one logical attempt.</summary>
    public void RecordAttempt(
        ProviderId provider,
        ChannelType channel,
        AttemptOutcome outcome,
        TimeSpan duration)
    {
        TagList tags = new()
        {
            { "provider", provider.Value },
            { "channel", channel.ToString() },
            { "outcome", outcome.ToString() },
        };

        _attempts.Add(1, tags);
        _duration.Record(duration.TotalSeconds, tags);
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
