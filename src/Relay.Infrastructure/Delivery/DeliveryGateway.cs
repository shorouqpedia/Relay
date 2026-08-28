using System.Diagnostics;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using ApplicationReceiptStatus = Relay.Application.Delivery.ReceiptStatus;
using ProviderReceiptStatus = Relay.Providers.Abstractions.ReceiptStatus;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Routes an application-level delivery request to the right provider instance.
/// </summary>
/// <remarks>
/// The other half of the seam. <see cref="ProviderRegistry"/> translates what a
/// provider <em>is</em>; this translates what it <em>does</em>.
/// <para>
/// The <see cref="IMessageProvider"/> instances injected here are the fully
/// decorated ones — logging, metrics, rate limiting, resilience — because the
/// container was told to resolve the interface and the decorators are what is
/// registered against it (ADR 0007). Resolving a concrete provider type anywhere
/// would silently bypass all of them, which is why nothing does.
/// </para>
/// </remarks>
internal sealed class DeliveryGateway(IEnumerable<IMessageProvider> providers)
    : IDeliveryGateway, IReceiptQuery
{
    private readonly Dictionary<string, IMessageProvider> _providers =
        providers.ToDictionary(p => p.Descriptor.Id.Value, StringComparer.Ordinal);

    public async Task<DeliveryOutcome> SendAsync(
        ProviderId provider,
        Message message,
        CancellationToken cancellationToken)
    {
        if (!_providers.TryGetValue(provider.Value, out IMessageProvider? implementation))
        {
            // The router chose a provider that is not registered here. Only
            // reachable if the registry and the container disagree, which would be
            // a wiring bug — but it is reported as a transient failure rather than
            // thrown, so one misrouted message does not stop the loop.
            return new DeliveryOutcome(
                AttemptOutcome.TransientFailure,
                ProviderMessageId: null,
                FailureReason: $"Provider '{provider}' is not registered in this process.",
                RetryAfter: null,
                Duration: TimeSpan.Zero);
        }

        var request = new DeliveryRequest(
            message.Id,
            message.Recipient,
            message.Body,
            message.IdempotencyKey,
            message.AttemptCount + 1);

        // Measured here rather than inside the provider, so the duration recorded
        // on the attempt is what the caller experienced — including everything the
        // decorator chain added. Per-try latency is a separate measurement taken
        // by the resilience decorator itself.
        long start = Stopwatch.GetTimestamp();

        DeliveryResult result = await implementation
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return new DeliveryOutcome(
            result.Outcome,
            result.ProviderMessageId,
            result.FailureReason,
            result.RetryAfter,
            Stopwatch.GetElapsedTime(start));
    }

    public async Task<ApplicationReceiptStatus> QueryAsync(
        ProviderId provider,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        // Unknown covers both "cannot ask" and "asked, and it does not know".
        // Collapsing them is deliberate: the caller does the same thing either
        // way, because no further information is coming.
        if (!_providers.TryGetValue(provider.Value, out IMessageProvider? implementation)
            || implementation is not IReceiptQueryable queryable)
        {
            return ApplicationReceiptStatus.Unknown;
        }

        ProviderReceiptStatus status = await queryable
            .QueryReceiptAsync(providerMessageId, cancellationToken)
            .ConfigureAwait(false);

        return status switch
        {
            ProviderReceiptStatus.Pending => ApplicationReceiptStatus.Pending,
            ProviderReceiptStatus.Delivered => ApplicationReceiptStatus.Delivered,
            ProviderReceiptStatus.Failed => ApplicationReceiptStatus.Failed,
            _ => ApplicationReceiptStatus.Unknown,
        };
    }
}
