using Relay.Domain.Common;
using Relay.Domain.Messaging.Events;

namespace Relay.Domain.Messaging;

/// <summary>
/// A message submitted for delivery, and everything known about what happened to it.
/// </summary>
/// <remarks>
/// The aggregate root. There are no public setters and no public constructor:
/// every state change is a named transition that checks it is legal first
/// (ADR 0004), and reports an illegal one as a <see cref="Result"/> rather than
/// an exception, because a late receipt or a repeated cancellation is a normal
/// event in a healthy system, not a fault (ADR 0005).
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

    /// <summary>The untyped id the outbox needs. Domain code uses <see cref="Entity{TId}.Id"/>.</summary>
    public override Guid AggregateId => Id.Value;

    public IdempotencyKey IdempotencyKey { get; private init; }

    public Recipient Recipient { get; private init; }

    public MessageBody Body { get; private init; }

    /// <summary>Derived from the recipient so the two cannot disagree.</summary>
    public ChannelType Channel => Recipient.Channel;

    public MessageStatus Status { get; private set; }

    public int MaxAttempts { get; private init; }

    /// <summary>The provider currently handling, or last to have handled, this message.</summary>
    public ProviderId? CurrentProviderId { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    /// <summary>
    /// When the current dispatch began. This is how the sweeper finds messages
    /// whose worker died between claiming them and recording an outcome: nothing
    /// else will ever move them out of <see cref="MessageStatus.Dispatching"/>.
    /// </summary>
    public DateTimeOffset? DispatchStartedAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Every attempt made, oldest first.</summary>
    public IReadOnlyList<DeliveryAttempt> Attempts => _attempts.AsReadOnly();

    public int AttemptCount => _attempts.Count;

    public bool IsTerminal => Status
        is MessageStatus.Delivered
        or MessageStatus.Failed
        or MessageStatus.DeadLettered
        or MessageStatus.Cancelled;

    public bool HasAttemptsRemaining => AttemptCount < MaxAttempts;

    /// <summary>
    /// The only way a <see cref="Message"/> comes into existence. The arguments
    /// are already validated value objects, so only the retry budget is left to check.
    /// </summary>
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
    /// Claims the message for a provider. Done before the provider is called, so a
    /// second worker picking up the same message loses the concurrency check
    /// rather than sending a duplicate.
    /// </summary>
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

    /// <summary>Records what happened on an attempt and moves the message accordingly.</summary>
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
    /// Applies a delivery receipt. A duplicate receipt succeeds without changing
    /// anything — telling the provider its retry failed would only make it retry
    /// again. A receipt for a message in any other terminal state lost a race
    /// against the sweeper and is reported as a conflict.
    /// </summary>
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

    /// <summary>Applies a negative receipt: accepted by the provider, then found undeliverable.</summary>
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
    /// The message may already have reached the provider, so this risks a
    /// duplicate send; the alternative is a message no worker will ever pick up
    /// again (ADR 0008). The lost attempt is charged to the retry budget so a
    /// message that keeps killing its worker is not released forever.
    /// </summary>
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
    /// Gives up waiting for an outcome. The message may well have been delivered;
    /// what is recorded is that Relay stopped waiting to find out.
    /// </summary>
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
    /// Withdraws a message not yet handed to a provider. Once a provider has it
    /// there is no way to unsend, and reporting success would be a lie.
    /// </summary>
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
