using Relay.Domain.Common;
using Relay.Domain.Messaging;
using Relay.Domain.Messaging.Events;
using Relay.Domain.UnitTests.Builders;

namespace Relay.Domain.UnitTests.Messaging;

/// <summary>
/// The state machine in <see cref="MessageStatus"/>, asserted transition by transition.
/// </summary>
/// <remarks>
/// Test names carry a scenario id (<c>ML01</c>, <c>ML02</c>…) matching the rows in
/// <c>docs/test-plan.md</c>. A failing test name therefore points at a numbered
/// scenario rather than only at a method, which is what makes a red build
/// answerable by someone who did not write the test.
/// </remarks>
public sealed class MessageLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ML01_Submit_WithValidInput_StartsPendingAndRaisesQueued()
    {
        Message message = new MessageBuilder().Build();

        message.Status.ShouldBe(MessageStatus.Pending);
        message.AttemptCount.ShouldBe(0);
        message.CompletedAt.ShouldBeNull();
        message.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<MessageQueued>();
    }

    [Fact]
    public void ML02_Submit_WithZeroMaxAttempts_IsRejected()
    {
        IdempotencyKey key = IdempotencyKey.Create("idem-key-0001").Value;
        Recipient recipient = Recipient.Create(ChannelType.Email, "a@example.com").Value;
        MessageBody body = MessageBody.Create(ChannelType.Email, "Body").Value;

        Result<Message> result = Message.Submit(key, recipient, body, maxAttempts: 0, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.max_attempts_invalid");
    }

    [Fact]
    public void ML03_BeginDispatch_FromPending_MovesToDispatching()
    {
        Message message = new MessageBuilder().Build();
        ProviderId provider = ProviderId.Create("email.postal").Value;

        Result result = message.BeginDispatch(provider, Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Dispatching);
        message.CurrentProviderId.ShouldBe(provider);
        message.DispatchStartedAt.ShouldBe(Now);
    }

    [Fact]
    public void ML04_BeginDispatch_WhenAlreadyDispatching_IsRejected()
    {
        Message message = new MessageBuilder().BuildDispatching();
        ProviderId other = ProviderId.Create("email.mailhook").Value;

        Result result = message.BeginDispatch(other, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.invalid_transition");
        message.CurrentProviderId!.Value.ShouldBe("email.postal");
    }

    [Fact]
    public void ML05_RecordAttempt_Accepted_MovesToSentAndRaisesSent()
    {
        Message message = new MessageBuilder().BuildDispatching();

        Result result = message.RecordAttempt(
            AttemptOutcome.Accepted,
            providerMessageId: "pm-42",
            failureReason: null,
            duration: TimeSpan.FromMilliseconds(90),
            Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Sent);
        message.SentAt.ShouldBe(Now);
        message.AttemptCount.ShouldBe(1);
        message.Attempts[0].ProviderMessageId.ShouldBe("pm-42");
        message.DomainEvents.OfType<MessageSent>().ShouldHaveSingleItem();
    }

    [Fact]
    public void ML06_RecordAttempt_Rejected_MovesToFailedWithoutConsumingRetries()
    {
        Message message = new MessageBuilder().WithMaxAttempts(3).BuildDispatching();

        message.RecordAttempt(
            AttemptOutcome.Rejected,
            providerMessageId: null,
            failureReason: "recipient blocked",
            duration: TimeSpan.FromMilliseconds(40),
            Now);

        message.Status.ShouldBe(MessageStatus.Failed);
        message.FailureReason.ShouldBe("recipient blocked");
        message.CompletedAt.ShouldBe(Now);
        message.IsTerminal.ShouldBeTrue();
    }

    [Theory]
    [InlineData(AttemptOutcome.TransientFailure)]
    [InlineData(AttemptOutcome.RateLimited)]
    [InlineData(AttemptOutcome.Timeout)]
    public void ML07_RecordAttempt_RetryableOutcome_ReturnsToPendingWhileBudgetRemains(
        AttemptOutcome outcome)
    {
        Message message = new MessageBuilder().WithMaxAttempts(3).BuildDispatching();

        message.RecordAttempt(outcome, null, "upstream hiccup", TimeSpan.Zero, Now);

        message.Status.ShouldBe(MessageStatus.Pending);
        message.CurrentProviderId.ShouldBeNull();
        message.DispatchStartedAt.ShouldBeNull();
        message.AttemptCount.ShouldBe(1);
        message.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void ML08_RecordAttempt_RetryableOutcome_DeadLettersWhenBudgetSpent()
    {
        Message message = new MessageBuilder().WithMaxAttempts(2).BuildDeadLettered();

        message.Status.ShouldBe(MessageStatus.DeadLettered);
        message.AttemptCount.ShouldBe(2);
        message.CompletedAt.ShouldNotBeNull();
        message.DomainEvents.OfType<MessageDeadLettered>().ShouldHaveSingleItem();
    }

    [Fact]
    public void ML09_RecordAttempt_WhenNotDispatching_IsRejected()
    {
        Message message = new MessageBuilder().Build();

        Result result = message.RecordAttempt(
            AttemptOutcome.Accepted, "pm-1", null, TimeSpan.Zero, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.invalid_transition");
        message.AttemptCount.ShouldBe(0);
    }

    [Fact]
    public void ML10_RecordAttempt_WithNoOutcome_IsRejectedAndRecordsNothing()
    {
        Message message = new MessageBuilder().BuildDispatching();

        Result result = message.RecordAttempt(
            AttemptOutcome.None, null, null, TimeSpan.Zero, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.attempt_outcome_missing");
        message.AttemptCount.ShouldBe(0);
        message.Status.ShouldBe(MessageStatus.Dispatching);
    }

    [Fact]
    public void ML11_Attempts_AreNumberedInOrderFromOne()
    {
        Message message = new MessageBuilder().WithMaxAttempts(3).BuildDeadLettered();

        message.Attempts.Select(a => a.Sequence).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void ML12_Cancel_FromPending_MovesToCancelled()
    {
        Message message = new MessageBuilder().Build();

        Result result = message.Cancel(Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Cancelled);
        message.DomainEvents.OfType<MessageCancelled>().ShouldHaveSingleItem();
    }

    [Fact]
    public void ML13_Cancel_AfterDispatchBegan_IsRejected()
    {
        Message message = new MessageBuilder().BuildDispatching();

        Result result = message.Cancel(Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.invalid_transition");
        message.Status.ShouldBe(MessageStatus.Dispatching);
    }

    [Fact]
    public void ML14_ReleaseStuckDispatch_ReturnsToPendingAndChargesAnAttempt()
    {
        Message message = new MessageBuilder().WithMaxAttempts(3).BuildDispatching();

        Result result = message.ReleaseStuckDispatch(Now.AddMinutes(10));

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Pending);
        message.AttemptCount.ShouldBe(1);
        message.Attempts[0].Outcome.ShouldBe(AttemptOutcome.Timeout);
    }

    [Fact]
    public void ML15_ReleaseStuckDispatch_WithNoBudgetLeft_DeadLetters()
    {
        Message message = new MessageBuilder().WithMaxAttempts(1).BuildDispatching();

        message.ReleaseStuckDispatch(Now.AddMinutes(10));

        message.Status.ShouldBe(MessageStatus.DeadLettered);
        message.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void ML16_Abandon_FromSent_DeadLetters()
    {
        Message message = new MessageBuilder().BuildSent();

        Result result = message.Abandon("no receipt within the expected window", Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.DeadLettered);
        message.FailureReason.ShouldBe("no receipt within the expected window");
    }

    [Fact]
    public void ML17_Abandon_WhenAlreadyTerminal_IsRejected()
    {
        Message message = new MessageBuilder().BuildDelivered();

        Result result = message.Abandon("sweeper gave up", Now);

        result.IsFailure.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Delivered);
    }

    [Fact]
    public void ML18_DrainDomainEvents_EmptiesTheCollection()
    {
        Message message = new MessageBuilder().Build();

        IReadOnlyCollection<IDomainEvent> drained = message.DrainDomainEvents();

        drained.Count.ShouldBe(1);
        message.DomainEvents.ShouldBeEmpty();
    }
}
