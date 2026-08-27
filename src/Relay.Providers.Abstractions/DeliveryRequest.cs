using Relay.Domain.Messaging;

namespace Relay.Providers.Abstractions;

/// <summary>
/// Everything a provider needs in order to send one message, and nothing else.
/// </summary>
/// <remarks>
/// Deliberately not the <see cref="Message"/> aggregate. Handing a provider the
/// aggregate would give it the ability to change delivery state — and a provider
/// assembly deciding that a message is delivered is exactly the coupling the
/// plugin boundary exists to prevent (ADR 0003).
/// <para>
/// It also keeps the contract honest: everything a provider is allowed to know is
/// visible in this one type, so adding a field is a deliberate widening of the
/// boundary rather than an accident of passing a larger object.
/// </para>
/// </remarks>
/// <param name="MessageId">Relay's identifier for the message, for correlation and logging.</param>
/// <param name="Recipient">Where it is going.</param>
/// <param name="Body">What to send.</param>
/// <param name="IdempotencyKey">
/// Relay's key for this submission. A provider declaring
/// <see cref="ProviderCapabilities.NativeIdempotency"/> passes it upstream; the
/// rest ignore it.
/// </param>
/// <param name="Attempt">Which attempt this is, starting at one. Some providers vary behaviour on a retry.</param>
public sealed record DeliveryRequest(
    MessageId MessageId,
    Recipient Recipient,
    MessageBody Body,
    IdempotencyKey IdempotencyKey,
    int Attempt);
