using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Relay.Application.Delivery;
using Relay.Application.UnitTests.Builders;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.UnitTests.Delivery;

/// <summary>
/// Dispatching one message, and the ordering that makes a crash recoverable.
/// </summary>
/// <remarks>Scenario ids <c>DP01</c>–<c>DP07</c> in <c>docs/test-plan.md</c>.</remarks>
public sealed class MessageDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DP01_Dispatch_WhenProviderAccepts_MarksTheMessageSent()
    {
        var harness = new Harness().WithOutcome(AttemptOutcome.Accepted, providerMessageId: "pm-9");
        Message message = new MessageBuilder().Build();

        Result<AttemptOutcome> result = await harness.Dispatcher
            .DispatchAsync(message, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(AttemptOutcome.Accepted);
        message.Status.ShouldBe(MessageStatus.Sent);
        message.Attempts.Single().ProviderMessageId.ShouldBe("pm-9");
    }

    [Fact]
    public async Task DP02_Dispatch_CommitsTheClaimBeforeCallingTheProvider()
    {
        var harness = new Harness().WithOutcome(AttemptOutcome.Accepted);
        Message message = new MessageBuilder().Build();

        await harness.Dispatcher.DispatchAsync(message, TestContext.Current.CancellationToken);

        // The ordering is the whole point. A process that dies during the provider
        // call must leave a row in Dispatching for the recovery loop to find; if
        // the claim were committed afterwards, the message would still read as
        // Pending despite possibly having been sent, and the next worker would
        // send it again with nothing recording that it had been.
        harness.Gateway.SavesBeforeSend.ShouldBe(1);
        harness.UnitOfWork.SaveCount.ShouldBe(2);
    }

    [Fact]
    public async Task DP03_Dispatch_WhenProviderRejects_MarksTheMessageFailed()
    {
        var harness = new Harness().WithOutcome(AttemptOutcome.Rejected, failureReason: "blocked");
        Message message = new MessageBuilder().Build();

        await harness.Dispatcher.DispatchAsync(message, TestContext.Current.CancellationToken);

        message.Status.ShouldBe(MessageStatus.Failed);
        message.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public async Task DP04_Dispatch_WhenTransientAndBudgetRemains_ReturnsTheMessageToPending()
    {
        var harness = new Harness().WithOutcome(AttemptOutcome.TransientFailure, failureReason: "502");
        Message message = new MessageBuilder().WithMaxAttempts(3).Build();

        await harness.Dispatcher.DispatchAsync(message, TestContext.Current.CancellationToken);

        message.Status.ShouldBe(MessageStatus.Pending);
        message.CurrentProviderId.ShouldBeNull();
    }

    [Fact]
    public async Task DP05_Dispatch_RecordsTheOutcomeAgainstProviderHealth()
    {
        var harness = new Harness().WithOutcome(
            AttemptOutcome.RateLimited,
            retryAfter: TimeSpan.FromSeconds(30));

        await harness.Dispatcher.DispatchAsync(
            new MessageBuilder().Build(), TestContext.Current.CancellationToken);

        // Health has to learn from every attempt, not only from failures the
        // router happened to notice. This is what closes the loop between one
        // message's failure and the next message's routing.
        harness.Health.Recorded.ShouldHaveSingleItem();
        harness.Health.Recorded[0].Outcome.ShouldBe(AttemptOutcome.RateLimited);
        harness.Health.Recorded[0].RetryAfter.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task DP06_Dispatch_WhenNoProviderIsAvailable_LeavesTheMessageUntouched()
    {
        var harness = new Harness().WithNoProviders();
        Message message = new MessageBuilder().Build();

        Result<AttemptOutcome> result = await harness.Dispatcher
            .DispatchAsync(message, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        message.Status.ShouldBe(MessageStatus.Pending);
        message.AttemptCount.ShouldBe(0);
        harness.UnitOfWork.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task DP07_Dispatch_WhenTheMessageIsNoLongerPending_DoesNotCallTheProvider()
    {
        var harness = new Harness().WithOutcome(AttemptOutcome.Accepted);
        Message message = new MessageBuilder().Build();
        message.Cancel(Now);

        Result<AttemptOutcome> result = await harness.Dispatcher
            .DispatchAsync(message, TestContext.Current.CancellationToken);

        // Cancelled between being claimed from the database and reaching here. The
        // provider must not be called: the send would be real and unrecordable.
        result.IsFailure.ShouldBeTrue();
        harness.Gateway.SendCount.ShouldBe(0);
    }

    private sealed class Harness
    {
        private readonly List<ProviderProfile> _providers =
        [
            new(ProviderId.Create("email.postal").Value, ChannelType.Email, TimeSpan.FromHours(6), 1),
        ];

        private DeliveryOutcome _outcome = new(
            AttemptOutcome.Accepted, "pm-1", null, null, TimeSpan.FromMilliseconds(10));

        public RecordingUnitOfWork UnitOfWork { get; } = new();

        public RecordingGateway Gateway { get; private set; } = null!;

        public RecordingHealth Health { get; } = new();

        public MessageDispatcher Dispatcher => Build();

        public Harness WithOutcome(
            AttemptOutcome outcome,
            string? providerMessageId = null,
            string? failureReason = null,
            TimeSpan? retryAfter = null)
        {
            _outcome = new DeliveryOutcome(
                outcome,
                providerMessageId,
                failureReason,
                retryAfter,
                TimeSpan.FromMilliseconds(10));

            return this;
        }

        public Harness WithNoProviders()
        {
            _providers.Clear();
            return this;
        }

        private MessageDispatcher Build()
        {
            Gateway = new RecordingGateway(_outcome, UnitOfWork);

            var registry = new StubRegistry(_providers);
            var router = new ProviderRouter(registry, Health, NullLogger<ProviderRouter>.Instance);

            return new MessageDispatcher(
                router,
                Gateway,
                Health,
                UnitOfWork,
                new FakeTimeProvider(Now),
                NullLogger<MessageDispatcher>.Instance);
        }
    }

    private sealed class StubRegistry(List<ProviderProfile> providers) : IProviderRegistry
    {
        public IReadOnlyList<ProviderProfile> All => providers;

        public IReadOnlyList<ProviderProfile> For(ChannelType channel) =>
            [.. providers.Where(p => p.Channel == channel)];

        public ProviderProfile? Find(ProviderId id) => providers.FirstOrDefault(p => p.Id == id);
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class RecordingGateway(DeliveryOutcome outcome, RecordingUnitOfWork unitOfWork)
        : IDeliveryGateway
    {
        public int SendCount { get; private set; }

        /// <summary>How many commits had happened by the time the provider was called.</summary>
        public int SavesBeforeSend { get; private set; }

        public Task<DeliveryOutcome> SendAsync(
            ProviderId provider,
            Message message,
            CancellationToken cancellationToken)
        {
            if (SendCount == 0)
            {
                SavesBeforeSend = unitOfWork.SaveCount;
            }

            SendCount++;
            return Task.FromResult(outcome);
        }
    }

    private sealed class RecordingHealth : IProviderHealth
    {
        public List<(ProviderId Provider, AttemptOutcome Outcome, TimeSpan? RetryAfter)> Recorded { get; } = [];

        public bool IsAvailable(ProviderId provider) => true;

        public void Record(ProviderId provider, AttemptOutcome outcome, TimeSpan? retryAfter = null) =>
            Recorded.Add((provider, outcome, retryAfter));
    }
}
