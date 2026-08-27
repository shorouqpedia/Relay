using Relay.Domain.Messaging;

namespace Relay.Providers.Abstractions;

/// <summary>
/// What a provider made of a delivery request.
/// </summary>
/// <remarks>
/// A provider returns this. It does not throw — see the contract on
/// <see cref="IMessageProvider.SendAsync"/> for why, and what that costs.
/// <para>
/// The whole job of a provider assembly is turning one upstream's vocabulary into
/// this shape. Upstreams disagree about almost everything: some report a quota
/// breach as HTTP 429, others as a 200 with an error code in the body, others as a
/// 403. Every one of those becomes <see cref="AttemptOutcome.RateLimited"/> here,
/// and the core flow never learns which upstream it was talking to.
/// </para>
/// </remarks>
public sealed record DeliveryResult
{
    private DeliveryResult(
        AttemptOutcome outcome,
        string? providerMessageId,
        string? failureReason,
        TimeSpan? retryAfter)
    {
        Outcome = outcome;
        ProviderMessageId = providerMessageId;
        FailureReason = failureReason;
        RetryAfter = retryAfter;
    }

    /// <summary>How the attempt ended, in Relay's vocabulary rather than the provider's.</summary>
    public AttemptOutcome Outcome { get; }

    /// <summary>The provider's identifier for the message, when it gave one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>Why it failed, verbatim from the provider where available.</summary>
    /// <remarks>
    /// Verbatim is the point — this is what someone reads at 3am. It must not
    /// contain credentials or recipient details; masking is the provider's
    /// responsibility and the contract suite checks it.
    /// </remarks>
    public string? FailureReason { get; }

    /// <summary>
    /// How long the provider asked to be left alone, when it said.
    /// </summary>
    /// <remarks>
    /// Honoured by the rate-limit decorator. A provider that reports this and is
    /// ignored will escalate from refusing requests to blocking the account.
    /// </remarks>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The provider took the message.</summary>
    public static DeliveryResult Accepted(string? providerMessageId = null) =>
        new(AttemptOutcome.Accepted, providerMessageId, null, null);

    /// <summary>The provider refused, and will refuse again. Do not retry.</summary>
    public static DeliveryResult Rejected(string reason) =>
        new(AttemptOutcome.Rejected, null, reason, null);

    /// <summary>Something went wrong that may not go wrong again.</summary>
    public static DeliveryResult TransientFailure(string reason) =>
        new(AttemptOutcome.TransientFailure, null, reason, null);

    /// <summary>A quota was reached.</summary>
    public static DeliveryResult RateLimited(string reason, TimeSpan? retryAfter = null) =>
        new(AttemptOutcome.RateLimited, null, reason, retryAfter);

    /// <summary>
    /// No answer arrived in time. The message may or may not have been sent.
    /// </summary>
    /// <remarks>
    /// The only genuinely ambiguous outcome, and the reason every write path has
    /// to be safe to repeat (ADR 0008).
    /// </remarks>
    public static DeliveryResult Timeout(string reason) =>
        new(AttemptOutcome.Timeout, null, reason, null);
}
