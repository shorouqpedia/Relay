using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// How a delivery attempt ended, from Relay's point of view.
/// </summary>
public enum AttemptOutcome
{
    /// <summary>Unset. Present so that a default-initialised value is invalid rather than meaningful.</summary>
    None = 0,

    /// <summary>The provider accepted the message.</summary>
    Accepted = 1,

    /// <summary>The provider refused in a way that will not change on retry.</summary>
    Rejected = 2,

    /// <summary>The attempt failed for a reason that may not recur.</summary>
    TransientFailure = 3,

    /// <summary>The provider refused because a quota was reached.</summary>
    RateLimited = 4,

    /// <summary>No response arrived in time. The message may or may not have been sent.</summary>
    Timeout = 5,
}

/// <summary>
/// One recorded attempt to hand a message to a provider.
/// </summary>
/// <remarks>
/// Attempts are append-only: they are facts about what happened, and a fact does
/// not change. This is what makes the delivery history usable as an audit trail
/// and what lets the reconciliation sweeper work out what to ask a provider
/// about.
/// <para>
/// An attempt is inside the <see cref="Message"/> aggregate and has no repository
/// of its own. It is never loaded or saved independently, because an attempt
/// without its message is not a meaningful thing to hold.
/// </para>
/// <para>
/// <see cref="Outcome"/> being <see cref="AttemptOutcome.Timeout"/> is the case
/// worth understanding: it means the message may have been sent. Retrying is
/// therefore a decision to risk a duplicate in exchange for not risking a silent
/// loss, which is the trade ADR 0008 makes deliberately.
/// </para>
/// </remarks>
public sealed class DeliveryAttempt : Entity<Guid>
{
    private DeliveryAttempt(
        Guid id,
        int sequence,
        ProviderId providerId,
        AttemptOutcome outcome,
        string? providerMessageId,
        string? failureReason,
        TimeSpan duration,
        DateTimeOffset attemptedAt)
        : base(id)
    {
        Sequence = sequence;
        ProviderId = providerId;
        Outcome = outcome;
        ProviderMessageId = providerMessageId;
        FailureReason = failureReason;
        Duration = duration;
        AttemptedAt = attemptedAt;
    }

    private DeliveryAttempt()
    {
        ProviderId = null!;
    }

    /// <summary>Position in the attempt sequence for this message, starting at one.</summary>
    public int Sequence { get; private init; }

    /// <summary>Which provider was tried.</summary>
    public ProviderId ProviderId { get; private init; }

    /// <summary>How the attempt ended.</summary>
    public AttemptOutcome Outcome { get; private init; }

    /// <summary>
    /// The provider's own identifier for the message, when it gave one.
    /// </summary>
    /// <remarks>
    /// This is the join key for delivery receipts. A provider that accepts a
    /// message without returning an identifier cannot be reconciled, which is a
    /// property of that provider worth knowing about rather than hiding.
    /// </remarks>
    public string? ProviderMessageId { get; private init; }

    /// <summary>Why the attempt failed, verbatim from the provider where available.</summary>
    public string? FailureReason { get; private init; }

    /// <summary>How long the physical call took.</summary>
    public TimeSpan Duration { get; private init; }

    /// <summary>When the attempt was made, in UTC.</summary>
    public DateTimeOffset AttemptedAt { get; private init; }

    /// <summary>Whether this outcome leaves room for another attempt.</summary>
    public bool IsRetryable => Outcome
        is AttemptOutcome.TransientFailure
        or AttemptOutcome.RateLimited
        or AttemptOutcome.Timeout;

    internal static DeliveryAttempt Record(
        int sequence,
        ProviderId providerId,
        AttemptOutcome outcome,
        string? providerMessageId,
        string? failureReason,
        TimeSpan duration,
        DateTimeOffset attemptedAt) =>
        new(
            Guid.CreateVersion7(),
            sequence,
            providerId,
            outcome,
            providerMessageId,
            failureReason,
            duration,
            attemptedAt);
}
