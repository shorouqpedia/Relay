using Microsoft.Extensions.DependencyInjection;
using Relay.Application.Callbacks;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Infrastructure.Callbacks;

/// <summary>
/// Resolves the right provider's verifier and translates its answer.
/// </summary>
/// <remarks>
/// The seam for inbound callbacks, matching <c>DeliveryGateway</c> on the
/// outbound side. Receivers are registered keyed by provider id, so a route
/// segment resolves one without this class — or the host — naming any provider.
/// <para>
/// Keyed resolution is used rather than a dictionary built at startup because the
/// receivers are scoped: they read options, and a singleton dictionary would
/// capture a scoped service and hold configuration from whenever the process
/// started.
/// </para>
/// </remarks>
internal sealed class CallbackGateway(
    IServiceProvider services,
    IEnumerable<IMessageProvider> providers) : ICallbackGateway
{
    private readonly HashSet<string> _receiving =
    [
        .. providers
            .Where(p => p.Descriptor.Supports(ProviderCapabilities.PushesDeliveryReceipts))
            .Select(p => p.Descriptor.Id.Value),
    ];

    public bool CanReceive(ProviderId provider) => _receiving.Contains(provider.Value);

    public CallbackVerification Verify(
        ProviderId provider,
        CallbackSubmission submission,
        DateTimeOffset now)
    {
        ICallbackReceiver? receiver =
            services.GetKeyedService<ICallbackReceiver>(provider.Value);

        if (receiver is null)
        {
            // The descriptor claims PushesDeliveryReceipts and no receiver is
            // registered. A wiring fault in a provider assembly rather than
            // anything about this request — reported as a refusal so the endpoint
            // stays uniform, and loudly enough in the detail to be found.
            return new CallbackVerification(
                CallbackVerdictKind.Rejected,
                [],
                $"Provider '{provider}' declares PushesDeliveryReceipts but registered no "
                + "ICallbackReceiver. Callbacks for it cannot be verified.");
        }

        CallbackOutcome outcome = receiver.Receive(new InboundCallback(
            submission.Headers,
            submission.Body,
            now));

        return new CallbackVerification(
            Translate(outcome.Verdict),
            [.. outcome.Receipts.Select(Translate)],
            outcome.Detail);
    }

    private static CallbackVerdictKind Translate(CallbackVerdict verdict) => verdict switch
    {
        CallbackVerdict.Accepted => CallbackVerdictKind.Accepted,
        CallbackVerdict.Malformed => CallbackVerdictKind.Malformed,
        _ => CallbackVerdictKind.Rejected,
    };

    private static DeliveryReport Translate(ProviderReceipt receipt) => new(
        receipt.ProviderMessageId,
        receipt.Status switch
        {
            ReceiptStatus.Delivered => DeliveryReportStatus.Delivered,
            ReceiptStatus.Failed => DeliveryReportStatus.Failed,
            _ => DeliveryReportStatus.None,
        },
        receipt.Reason,
        receipt.OccurredAt);
}
