using Relay.Domain.Common;
using Relay.Domain.Messaging;
using Relay.Domain.Messaging.Events;
using Relay.Domain.UnitTests.Builders;

namespace Relay.Domain.UnitTests.Messaging;

/// <summary>
/// Delivery receipts arriving out of band, out of order, and more than once.
/// </summary>
/// <remarks>
/// These are the scenarios ADR 0008 exists for, and they are the ones a naive
/// implementation gets wrong: a duplicate receipt that returns an error causes the
/// provider to retry it, and a late receipt that is allowed through would resurrect
/// a message the system has already reported as finished.
/// <para>
/// Scenario ids <c>DR01</c>–<c>DR08</c> in <c>docs/test-plan.md</c>.
/// </para>
/// </remarks>
public sealed class DeliveryReceiptTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DR01_ConfirmDelivered_FromSent_MovesToDelivered()
    {
        Message message = new MessageBuilder().BuildSent();

        Result result = message.ConfirmDelivered(Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Delivered);
        message.CompletedAt.ShouldBe(Now);
        message.DomainEvents.OfType<MessageDelivered>().ShouldHaveSingleItem();
    }

    [Fact]
    public void DR02_ConfirmDelivered_Twice_SucceedsAndRaisesOneEvent()
    {
        Message message = new MessageBuilder().BuildSent();
        message.ConfirmDelivered(Now);

        Result second = message.ConfirmDelivered(Now.AddSeconds(30));

        // The second receipt succeeds rather than conflicting. A provider that
        // gets an error back will send the receipt again, so answering "already
        // done" is what actually stops the retries.
        second.IsSuccess.ShouldBeTrue();
        message.CompletedAt.ShouldBe(Now);
        message.DomainEvents.OfType<MessageDelivered>().Count().ShouldBe(1);
    }

    [Fact]
    public void DR03_ConfirmDelivered_BeforeAnyProviderAccepted_IsRejected()
    {
        Message message = new MessageBuilder().Build();

        Result result = message.ConfirmDelivered(Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("message.invalid_transition");
        message.Status.ShouldBe(MessageStatus.Pending);
    }

    [Fact]
    public void DR04_ConfirmDelivered_AfterDeadLettering_IsRejectedAndLeavesStateAlone()
    {
        Message message = new MessageBuilder().BuildSent();
        message.Abandon("no receipt within the expected window", Now);

        Result late = message.ConfirmDelivered(Now.AddHours(2));

        // The message really was delivered, and the sweeper was wrong to give up.
        // It still does not move: something already reported this message as
        // dead-lettered, and quietly reversing that would make the report a lie.
        // The caller records the receipt as an audit fact instead.
        late.IsFailure.ShouldBeTrue();
        late.Error.Code.ShouldBe("message.invalid_transition");
        message.Status.ShouldBe(MessageStatus.DeadLettered);
    }

    [Fact]
    public void DR05_ConfirmFailed_FromSent_MovesToFailed()
    {
        Message message = new MessageBuilder().BuildSent();

        Result result = message.ConfirmFailed("mailbox full", Now);

        result.IsSuccess.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Failed);
        message.FailureReason.ShouldBe("mailbox full");
    }

    [Fact]
    public void DR06_ConfirmFailed_Twice_SucceedsWithoutChangingCompletion()
    {
        Message message = new MessageBuilder().BuildSent();
        message.ConfirmFailed("mailbox full", Now);

        Result second = message.ConfirmFailed("mailbox full", Now.AddMinutes(5));

        second.IsSuccess.ShouldBeTrue();
        message.CompletedAt.ShouldBe(Now);
    }

    [Fact]
    public void DR07_ConfirmFailed_AfterDelivery_IsRejected()
    {
        Message message = new MessageBuilder().BuildDelivered();

        Result result = message.ConfirmFailed("bounced", Now.AddMinutes(1));

        result.IsFailure.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Delivered);
    }

    [Fact]
    public void DR08_ProviderMessageId_IsRecordedSoAReceiptCanBeMatchedBack()
    {
        Message message = new MessageBuilder().BuildSent(providerMessageId: "upstream-7f3a");

        // Without this, an inbound receipt has no way to find its message, and the
        // provider becomes unreconcilable regardless of what else it supports.
        message.Attempts.Single().ProviderMessageId.ShouldBe("upstream-7f3a");
    }
}
