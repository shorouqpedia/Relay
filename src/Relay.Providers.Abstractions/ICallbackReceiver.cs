namespace Relay.Providers.Abstractions;

/// <summary>
/// An inbound callback, exactly as it arrived.
/// </summary>
/// <remarks>
/// <see cref="Body"/> is the raw request body, unparsed. That is not a
/// convenience — a signature covers the bytes that were sent, and re-serializing
/// a deserialized object produces different bytes and a signature that can never
/// match. Verification has to happen before anything interprets the payload.
/// </remarks>
/// <param name="Headers">Request headers, matched case-insensitively.</param>
/// <param name="Body">The raw body.</param>
/// <param name="ReceivedAt">When Relay received it, for checking the timestamp window.</param>
public sealed record InboundCallback(
    IReadOnlyDictionary<string, string> Headers,
    string Body,
    DateTimeOffset ReceivedAt)
{
    /// <summary>Reads a header, or <see langword="null"/> when it is absent.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>Whether a callback may be acted on.</summary>
public enum CallbackVerdict
{
    /// <summary>Unset.</summary>
    None = 0,

    /// <summary>Signed correctly, recent, and understood.</summary>
    Accepted = 1,

    /// <summary>
    /// The signature did not verify, or the timestamp was outside the window.
    /// </summary>
    /// <remarks>
    /// One verdict for both, deliberately. The caller turns this into a 401 with
    /// no detail, and keeping them separate here would invite someone to report
    /// which one it was (ADR 0013).
    /// </remarks>
    Rejected = 2,

    /// <summary>Correctly signed, but the payload was not something this provider sends.</summary>
    Malformed = 3,
}

/// <summary>One delivery outcome reported by a provider.</summary>
/// <param name="ProviderMessageId">The provider's own identifier, as returned when it accepted the message.</param>
/// <param name="Status">What the provider says happened.</param>
/// <param name="Reason">Why, where the provider gave a reason.</param>
/// <param name="OccurredAt">When the provider says it happened, where it said.</param>
public sealed record ProviderReceipt(
    string ProviderMessageId,
    ReceiptStatus Status,
    string? Reason,
    DateTimeOffset? OccurredAt);

/// <summary>The result of examining a callback.</summary>
/// <param name="Verdict">Whether it may be acted on.</param>
/// <param name="Receipts">
/// The outcomes it carried. Several, because some providers batch — one callback
/// reporting fifty messages is normal, and modelling it as one would force those
/// providers to lie or to be special-cased.
/// </param>
/// <param name="Detail">Why it was rejected, for Relay's own logs only. Never returned to the caller.</param>
public sealed record CallbackOutcome(
    CallbackVerdict Verdict,
    IReadOnlyList<ProviderReceipt> Receipts,
    string? Detail)
{
    /// <summary>The callback verified and carried these receipts.</summary>
    public static CallbackOutcome Accepted(IReadOnlyList<ProviderReceipt> receipts) =>
        new(CallbackVerdict.Accepted, receipts, null);

    /// <summary>The signature or the timestamp did not hold up.</summary>
    public static CallbackOutcome Rejected(string detail) =>
        new(CallbackVerdict.Rejected, [], detail);

    /// <summary>Correctly signed, but not a payload this provider produces.</summary>
    public static CallbackOutcome Malformed(string detail) =>
        new(CallbackVerdict.Malformed, [], detail);
}

/// <summary>
/// A provider that reports delivery by calling Relay back.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMessageProvider"/> because not every provider does
/// this — Beacon and the webhook provider never report anything after accepting a
/// message — and folding it in would force them to implement a method they can
/// only answer with a rejection.
/// <para>
/// A provider implementing this must declare
/// <see cref="ProviderCapabilities.PushesDeliveryReceipts"/>. The contract suite
/// checks both directions: claiming it without implementing means callbacks
/// arrive at a provider that cannot read them, and implementing without claiming
/// means the sweeper abandons messages whose receipts were on their way.
/// </para>
/// <para>
/// Verification lives here rather than in shared middleware because every
/// provider signs differently — a different header, a different hash, a different
/// string to sign. A single implementation would end up as a switch on provider
/// id, which is the coupling the plugin boundary exists to remove (ADR 0003).
/// </para>
/// </remarks>
public interface ICallbackReceiver
{
    /// <summary>
    /// Verifies a callback and extracts what it reports.
    /// </summary>
    /// <remarks>
    /// Does not throw. A malformed payload is a verdict, not an exception — the
    /// endpoint receiving it is public, so anything that can be posted to it will
    /// be, and an exception per junk request is a denial-of-service surface as
    /// well as noise.
    /// </remarks>
    CallbackOutcome Receive(InboundCallback callback);
}
