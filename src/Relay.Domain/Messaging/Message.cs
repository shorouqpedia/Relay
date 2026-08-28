using Relay.Domain.Common;
using Relay.Domain.Messaging.Events;

namespace Relay.Domain.Messaging;

/// <summary>
/// A message submitted for delivery, and everything known about what happened to it.
/// </summary>
/// <remarks>
/// This is the aggregate root: the consistency boundary is a message together with
/// its delivery attempts, and nothing outside it can change either.
/// <para>
/// The type has no public setters and no public constructor. Every state change
/// goes through a named method that checks the transition is legal before making
/// it, so an invalid state is not something to be guarded against at call sites —
/// there is no API through which it can be reached. See ADR 0004.
/// </para>
/// <para>
/// Every transition method returns a <see cref="Result"/> rather than throwing.
/// Arriving too late is normal here: a delivery receipt can turn up after the
/// message was dead-lettered, and a caller can retry a cancellation on a message
/// that has since been sent. Those are expected outcomes of a healthy system, so
/// they are return values (ADR 0005).
/// </para>
/// </remarks>
public sealed class Message : AggregateRoot<MessageId>
{
    private readonly List<DeliveryAttempt> _attempts = [];

    private Message(
        MessageId id,
        IdempotencyKey idempotencyKey,
        Recipient recipient,
        MessageBody body,
        int maxAttempts,
        DateTimeOffset createdAt)
        : base(id)
    {
        IdempotencyKey = idempotencyKey;
        Recipient = recipient;
        Body = body;
        MaxAttempts = maxAttempts;
        CreatedAt = createdAt;
        Status = MessageStatus.Pending;

        Raise(new MessageQueued(id, recipient.Channel, createdAt));
    }

    private Message()
    {
        IdempotencyKey = null!;
        Recipient = null!;
        Body = null!;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Domain code uses <see cref="Entity{TId}.Id"/>, which is typed. This is the untyped form
    /// the outbox needs, and nothing in the domain should reach for it.
    /// </remarks>
    public override Guid AggregateId => Id.Value;

    /// <summary>The caller-supplied token that makes submission safe to repeat.</summary>
    public IdempotencyKey IdempotencyKey { get; private init; }

    /// <summary>Where the message is going.</summary>
    public Recipient Recipient { get; private init; }

    /// <summary>What the recipient will see.</summary>
    public MessageBody Body { get; private init; }

    /// <summary>The channel, derived from the recipient so the two cannot disagree.</summary>
    public ChannelType Channel => Recipient.Channel;

    /// <summary>Where the message is in its lifecycle.</summary>
    public MessageStatus Status { get; private set; }

    /// <summary>How many attempts this message is allowed in total.</summary>
    public int MaxAttempts { get; private init; }

    /// <summary>The provider currently handling, or last to have handled, this message.</summary>
    public ProviderId? CurrentProviderId { get; private set; }

    /// <summary>Why the message reached a terminal failure, when it did.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>When the message was accepted, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>
    /// When the current dispatch attempt began, if one is in progress.
    /// </summary>
    /// <remarks>
    /// A worker that dies between claiming a message and recording the attempt
    /// leaves it stuck in <see cref="MessageStatus.Dispatching"/> forever, because
    /// nothing else will ever move it. This timestamp is how the sweeper finds
    /// those: a message dispatching for longer than any provider call could take
    /// has lost its worker, not its provider.
    /// </remarks>
    public DateTimeOffset? DispatchStartedAt { get; private set; }

    /// <summary>When a provider accepted the message, if one has.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>When the message reached a terminal state, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Every attempt made, oldest first.</summary>
    public IReadOnlyList<DeliveryAttempt> Attempts => _attempts.AsReadOnly();

    /// <summary>How many attempts have been made.</summary>
    public int AttemptCount => _attempts.Count;

    /// <summary>Whether the lifecycle has ended. Nothing moves out of a terminal state.</summary>
    public bool IsTerminal => Status
        is MessageStatus.Delivered
        or MessageStatus.Failed
        or MessageStatus.DeadLettered
        or MessageStatus.Cancelled;

    /// <summary>Whether the retry budget still has room.</summary>
    public bool HasAttemptsRemaining => AttemptCount < MaxAttempts;

    /// <summary>
    /// Accepts a message for delivery.
    /// </summary>
    /// <remarks>
    /// The only way a <see cref="Message"/> comes into existence. Each argument is
    /// already a validated value object, so this method has nothing left to check
    /// beyond the retry budget — the parts that could be wrong were rejected before
    /// they became values.
    /// </remarks>
    public static Result<Message> Submit(
        IdempotencyKey idempotencyKey,
        Recipient recipient,
        MessageBody body,
        int maxAttempts,
        DateTimeOffset now)
    {
        if (maxAttempts < 1)
        {
            return Error.Validation(
                "message.max_attempts_invalid",
                "A message must be allowed at least one delivery attempt.");
        }

        return new Message(MessageId.New(), idempotencyKey, recipient, body, maxAttempts, now);
    }

    /// <summary>
    /// Claims the message for a delivery attempt by a specific provider.
    /// </summary>
    /// <remarks>
    /// Called by the worker before it talks to the provider, so that a second
    /// worker picking up the same message loses the optimistic concurrency check
    /// rather than sending a duplicate.
    /// </remarks>
    public Result BeginDispatch(ProviderId providerId, DateTimeOffset now)
    {
        if (Status is not MessageStatus.Pending)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Dispatching);
        }

        if (!HasAttemptsRemaining)
        {
            return MessageErrors.AttemptsExhausted(MaxAttempts);
        }

        Status = MessageStatus.Dispatching;
        CurrentProviderId = providerId;
        DispatchStartedAt = now;

        return Result.Success();
    }

    /// <summary>
    /// Records what happened on an attempt and moves the message accordingly.
    /// </summary>
    /// <remarks>
    /// This is the one method that decides the message's fate, and it is written as
    /// a single switch over the outcome so that every case is visible together.
    /// Splitting it per outcome would hide the fact that these five branches are
    /// exhaustive and mutually exclusive.
    /// </remarks>
    public Result RecordAttempt(
        AttemptOutcome outcome,
        string? providerMessageId,
        string? failureReason,
        TimeSpan duration,
        DateTimeOffset now)
    {
        if (Status is not MessageStatus.Dispatching)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Sent);
        }

        if (outcome is AttemptOutcome.None)
        {
            return Error.Validation(
                "message.attempt_outcome_missing",
                "A delivery attempt must record an outcome.");
        }

        ProviderId provider = CurrentProviderId!;

        _attempts.Add(DeliveryAttempt.Record(
            AttemptCount + 1,
            provider,
            outcome,
            providerMessageId,
            failureReason,
            duration,
            now));

        switch (outcome)
        {
            case AttemptOutcome.Accepted:
                Status = MessageStatus.Sent;
                SentAt = now;
                Raise(new MessageSent(Id, provider, providerMessageId, now));
                break;

            case AttemptOutcome.Rejected:
                Status = MessageStatus.Failed;
                FailureReason = failureReason;
                CompletedAt = now;
                Raise(new MessageFailed(Id, provider, failureReason, now));
                break;

            case AttemptOutcome.TransientFailure:
            case AttemptOutcome.RateLimited:
            case AttemptOutcome.Timeout:
                ReturnToQueueOrDeadLetter(failureReason, now);
                break;

            default:
                return Error.Failure(
                    "message.attempt_outcome_unhandled",
                    $"Outcome {outcome} has no handling. This is a bug.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Applies a delivery receipt from a provider.
    /// </summary>
    /// <remarks>
    /// Receipts arrive out of band and out of order. Three cases matter:
    /// <list type="bullet">
    /// <item>the message is <see cref="MessageStatus.Sent"/> — apply it</item>
    /// <item>the message is already <see cref="MessageStatus.Delivered"/> — a
    /// duplicate receipt, which succeeds without changing anything, because
    /// telling the provider its retry failed would only make it retry again</item>
    /// <item>the message is in some other terminal state — the receipt lost a race
    /// against the sweeper. It is a conflict, and the caller records it as an
    /// audit fact rather than moving the message</item>
    /// </list>
    /// </remarks>
    public Result ConfirmDelivered(DateTimeOffset now)
    {
        if (Status is MessageStatus.Delivered)
        {
            return Result.Success();
        }

        if (Status is not MessageStatus.Sent)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Delivered);
        }

        Status = MessageStatus.Delivered;
        CompletedAt = now;
        Raise(new MessageDelivered(Id, CurrentProviderId!, now));

        return Result.Success();
    }

    /// <summary>
    /// Applies a negative delivery receipt — the provider accepted the message and
    /// later found it undeliverable.
    /// </summary>
    public Result ConfirmFailed(string? reason, DateTimeOffset now)
    {
        if (Status is MessageStatus.Failed)
        {
            return Result.Success();
        }

        if (Status is not MessageStatus.Sent)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Failed);
        }

        Status = MessageStatus.Failed;
        FailureReason = reason;
        CompletedAt = now;
        Raise(new MessageFailed(Id, CurrentProviderId!, reason, now));

        return Result.Success();
    }

    /// <summary>
    /// Returns a message to the queue after the worker that claimed it disappeared.
    /// </summary>
    /// <remarks>
    /// The message may already have reached the provider — the worker died at an
    /// unknown point — so releasing it risks a duplicate send. It is still the
    /// better option: the alternative is a message that no worker will ever pick
    /// up again, which is a silent loss. This is the same trade as
    /// <see cref="AttemptOutcome.Timeout"/>, made for the same reason (ADR 0008).
    /// <para>
    /// The lost attempt is recorded so the retry budget still shrinks. Without
    /// that, a message that repeatedly kills its worker would be released forever.
    /// </para>
    /// </remarks>
    public Result ReleaseStuckDispatch(DateTimeOffset now)
    {
        if (Status is not MessageStatus.Dispatching)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Pending);
        }

        _attempts.Add(DeliveryAttempt.Record(
            AttemptCount + 1,
            CurrentProviderId!,
            AttemptOutcome.Timeout,
            providerMessageId: null,
            failureReason: "The worker handling this dispatch stopped without recording an outcome.",
            duration: now - DispatchStartedAt!.Value,
            attemptedAt: now));

        ReturnToQueueOrDeadLetter(
            "Dispatch was abandoned by its worker and the retry budget is spent.",
            now);

        return Result.Success();
    }

    /// <summary>
    /// Gives up on a message whose outcome never arrived.
    /// </summary>
    /// <remarks>
    /// Called by the reconciliation sweeper for messages sitting in
    /// <see cref="MessageStatus.Sent"/> past their receipt window. The message may
    /// well have been delivered; what is being recorded is that Relay stopped
    /// waiting to find out.
    /// </remarks>
    public Result Abandon(string reason, DateTimeOffset now)
    {
        if (IsTerminal)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.DeadLettered);
        }

        Status = MessageStatus.DeadLettered;
        FailureReason = reason;
        CompletedAt = now;
        Raise(new MessageDeadLettered(Id, AttemptCount, reason, now));

        return Result.Success();
    }

    /// <summary>
    /// Withdraws a message that has not been handed to a provider yet.
    /// </summary>
    /// <remarks>
    /// Only <see cref="MessageStatus.Pending"/> can be cancelled. Once a provider
    /// has the message, Relay has no way to unsend it, and reporting success would
    /// be a lie the caller would act on.
    /// </remarks>
    public Result Cancel(DateTimeOffset now)
    {
        if (Status is not MessageStatus.Pending)
        {
            return MessageErrors.InvalidTransition(Status, MessageStatus.Cancelled);
        }

        Status = MessageStatus.Cancelled;
        CompletedAt = now;
        Raise(new MessageCancelled(Id, now));

        return Result.Success();
    }

    private void ReturnToQueueOrDeadLetter(string? failureReason, DateTimeOffset now)
    {
        if (HasAttemptsRemaining)
        {
            Status = MessageStatus.Pending;
            CurrentProviderId = null;
            DispatchStartedAt = null;
            return;
        }

        Status = MessageStatus.DeadLettered;
        FailureReason = failureReason;
        CompletedAt = now;
        Raise(new MessageDeadLettered(Id, AttemptCount, failureReason, now));
    }
}
