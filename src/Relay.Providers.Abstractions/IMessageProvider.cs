namespace Relay.Providers.Abstractions;

/// <summary>
/// One way of getting a message to a recipient.
/// </summary>
/// <remarks>
/// This interface is the entire contract between Relay and a provider assembly.
/// It is two members because the surface is the boundary: anything added here has
/// to be implementable by every provider that will ever exist, and a member that
/// only some providers can honour becomes a capability flag instead
/// (<see cref="ProviderCapabilities"/>).
/// <para>
/// Implementations are wrapped in the decorator chain described in ADR 0007, so a
/// provider assembly contains no retry logic, no rate limiting, no metrics, and
/// no logging beyond what is specific to that upstream. If a provider is doing
/// any of that, the abstraction has leaked.
/// </para>
/// </remarks>
public interface IMessageProvider
{
    /// <summary>This provider's identity and declared abilities.</summary>
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Attempts to hand one message to the upstream.
    /// </summary>
    /// <remarks>
    /// <b>This method does not throw.</b> Every failure mode of the upstream —
    /// a refusal, a timeout, a malformed response, an SDK blowing up — comes back
    /// as a <see cref="DeliveryResult"/>. The only exceptions that may escape are
    /// <see cref="OperationCanceledException"/> from <paramref name="cancellationToken"/>
    /// and genuine programming errors.
    /// <para>
    /// The reason is that a provider's failures are not exceptional from Relay's
    /// point of view — they are the routine subject matter of the system, and the
    /// caller has to make a routing decision from them. An exception would be a
    /// worse channel for that: the caller would have to catch a type it cannot
    /// enumerate, and every provider would express "rate limited" differently.
    /// </para>
    /// <para>
    /// This is a real burden on implementers, and it is where the provider
    /// contract suite spends most of its assertions. It is also the property that
    /// makes the core flow provider-agnostic, so it is not negotiable per provider.
    /// </para>
    /// </remarks>
    /// <param name="request">What to send, and to whom.</param>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>How the attempt ended, in Relay's vocabulary.</returns>
    Task<DeliveryResult> SendAsync(DeliveryRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A provider that can be asked what became of a message it accepted.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMessageProvider"/> because most upstreams cannot do
/// this, and folding it in would force every provider to implement a method it
/// answers with "I don't know".
/// <para>
/// This is what turns the reconciliation sweeper from a thing that gives up into
/// a thing that finds out. A provider implementing it must declare
/// <see cref="ProviderCapabilities.QueryableReceipts"/>; the contract suite checks
/// that the declaration and the implementation agree, in both directions.
/// </para>
/// </remarks>
public interface IReceiptQueryable
{
    /// <summary>
    /// Asks the upstream for the current state of a message.
    /// </summary>
    /// <remarks>
    /// Does not throw, for the same reasons as
    /// <see cref="IMessageProvider.SendAsync"/>.
    /// </remarks>
    /// <param name="providerMessageId">The identifier the provider returned when it accepted the message.</param>
    /// <param name="cancellationToken">Abandons the query.</param>
    /// <returns>What the upstream says, including that it does not know.</returns>
    Task<ReceiptStatus> QueryReceiptAsync(string providerMessageId, CancellationToken cancellationToken);
}

/// <summary>What an upstream says about a message it accepted.</summary>
public enum ReceiptStatus
{
    /// <summary>Unset. Present so that a default-initialised value is invalid rather than meaningful.</summary>
    None = 0,

    /// <summary>Still in flight as far as the upstream knows.</summary>
    Pending = 1,

    /// <summary>The recipient received it.</summary>
    Delivered = 2,

    /// <summary>The upstream could not deliver it.</summary>
    Failed = 3,

    /// <summary>
    /// The upstream has no record of it.
    /// </summary>
    /// <remarks>
    /// Usually means the message aged out of the upstream's retention window
    /// rather than that it never existed — so it is not evidence of anything, and
    /// the sweeper treats it as a reason to stop asking rather than as a failure.
    /// </remarks>
    Unknown = 4,
}
