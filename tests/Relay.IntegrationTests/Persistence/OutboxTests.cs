using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Persistence.Outbox;

namespace Relay.IntegrationTests.Persistence;

/// <summary>
/// The outbox: atomic with the state change, and retried until it lands.
/// </summary>
/// <remarks>Scenario ids <c>OB01</c>–<c>OB06</c> in <c>docs/test-plan.md</c>.</remarks>
[Collection(PostgresCollection.Name)]
public sealed class OutboxTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OB01_SavingAnAggregate_WritesItsEventsInTheSameTransaction()
    {
        Message message = NewMessage();

        await using RelayDbContext context = postgres.CreateContext();
        context.Messages.Add(message);

        await new UnitOfWork(context, TimeProvider.System)
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        await using RelayDbContext read = postgres.CreateContext();
        List<OutboxMessage> rows = await read.OutboxMessages
            .Where(row => row.AggregateId == message.Id.Value)
            .ToListAsync(TestContext.Current.CancellationToken);

        // One transaction produced both the message row and the event row. There
        // is no ordering of two separate commits that achieves this, which is the
        // entire argument for the outbox.
        rows.ShouldHaveSingleItem();
        rows[0].Type.ShouldContain(nameof(Domain.Messaging.Events.MessageQueued));
        rows[0].ProcessedAt.ShouldBeNull();
    }

    [Fact]
    public async Task OB02_SavedAggregate_HasNoEventsLeftToRaiseTwice()
    {
        Message message = NewMessage();

        await using RelayDbContext context = postgres.CreateContext();
        context.Messages.Add(message);

        await new UnitOfWork(context, TimeProvider.System)
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        // Drained, not copied. A second save of the same tracked aggregate must not
        // produce the same event again — that would turn one state change into two
        // announcements, which no amount of consumer idempotency makes correct.
        message.DomainEvents.ShouldBeEmpty();

        await new UnitOfWork(context, TimeProvider.System)
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        await using RelayDbContext read = postgres.CreateContext();
        int count = await read.OutboxMessages
            .CountAsync(
                row => row.AggregateId == message.Id.Value,
                TestContext.Current.CancellationToken);

        count.ShouldBe(1);
    }

    [Fact]
    public async Task OB03_Dispatcher_PublishesPendingRowsAndMarksThemProcessed()
    {
        Message message = NewMessage();

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(message);
            await new UnitOfWork(write, TimeProvider.System)
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new RecordingPublisher();

        await using RelayDbContext context = postgres.CreateContext();
        await new OutboxDispatcher(
                context,
                publisher,
                TimeProvider.System,
                NullLogger<OutboxDispatcher>.Instance)
            .DispatchAsync(50, TestContext.Current.CancellationToken);

        publisher.Published.ShouldNotBeEmpty();

        await using RelayDbContext read = postgres.CreateContext();
        OutboxMessage row = await read.OutboxMessages
            .FirstAsync(
                r => r.AggregateId == message.Id.Value,
                TestContext.Current.CancellationToken);

        row.ProcessedAt.ShouldNotBeNull();
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task OB04_Dispatcher_LeavesAFailedRowPendingAndRecordsWhy()
    {
        Message message = NewMessage();

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(message);
            await new UnitOfWork(write, TimeProvider.System)
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new FailingPublisher("the broker refused the connection");

        await using RelayDbContext context = postgres.CreateContext();
        int published = await new OutboxDispatcher(
                context,
                publisher,
                TimeProvider.System,
                NullLogger<OutboxDispatcher>.Instance)
            .DispatchAsync(50, TestContext.Current.CancellationToken);

        published.ShouldBe(0);

        await using RelayDbContext read = postgres.CreateContext();
        OutboxMessage row = await read.OutboxMessages
            .FirstAsync(
                r => r.AggregateId == message.Id.Value,
                TestContext.Current.CancellationToken);

        // Still pending, with the reason attached. There is no dead-letter state:
        // an event that cannot be published is a bug to fix and replay, and moving
        // it aside would hide that it exists.
        row.ProcessedAt.ShouldBeNull();
        row.AttemptCount.ShouldBe(1);
        row.LastError.ShouldNotBeNull();
        row.LastError.ShouldContain("refused the connection");
    }

    [Fact]
    public async Task OB05_Dispatcher_OneFailingRowDoesNotStopTheRest()
    {
        Message first = NewMessage();
        Message second = NewMessage();

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(first);
            write.Messages.Add(second);
            await new UnitOfWork(write, TimeProvider.System)
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisher = new SelectivelyFailingPublisher(first.Id.Value.ToString());

        await using RelayDbContext context = postgres.CreateContext();
        await new OutboxDispatcher(
                context,
                publisher,
                TimeProvider.System,
                NullLogger<OutboxDispatcher>.Instance)
            .DispatchAsync(50, TestContext.Current.CancellationToken);

        await using RelayDbContext read = postgres.CreateContext();

        OutboxMessage failed = await read.OutboxMessages.FirstAsync(
            r => r.AggregateId == first.Id.Value,
            TestContext.Current.CancellationToken);
        OutboxMessage succeeded = await read.OutboxMessages.FirstAsync(
            r => r.AggregateId == second.Id.Value,
            TestContext.Current.CancellationToken);

        failed.ProcessedAt.ShouldBeNull();
        succeeded.ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task OB06_TwoDispatchers_DoNotPublishTheSameRow()
    {
        await using (RelayDbContext write = postgres.CreateContext())
        {
            for (int i = 0; i < 6; i++)
            {
                write.Messages.Add(NewMessage());
            }

            await new UnitOfWork(write, TimeProvider.System)
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var publisherOne = new RecordingPublisher();
        var publisherTwo = new RecordingPublisher();

        await using RelayDbContext one = postgres.CreateContext();
        await using RelayDbContext two = postgres.CreateContext();

        await using var txOne = await one.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var txTwo = await two.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        await new OutboxDispatcher(one, publisherOne, TimeProvider.System, NullLogger<OutboxDispatcher>.Instance)
            .DispatchAsync(3, TestContext.Current.CancellationToken);
        await new OutboxDispatcher(two, publisherTwo, TimeProvider.System, NullLogger<OutboxDispatcher>.Instance)
            .DispatchAsync(3, TestContext.Current.CancellationToken);

        // Duplicate publication is the failure mode that makes a scaled-out
        // dispatcher worse than a single one. SKIP LOCKED is what prevents it.
        publisherOne.Published.ShouldNotBeEmpty();
        publisherTwo.Published.ShouldNotBeEmpty();
        publisherOne.Published.Intersect(publisherTwo.Published).ShouldBeEmpty();

        await txOne.RollbackAsync(TestContext.Current.CancellationToken);
        await txTwo.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static Message NewMessage() => Message.Submit(
        IdempotencyKey.Create($"idem-{Guid.CreateVersion7():N}").Value,
        Recipient.Create(ChannelType.Email, "outbox@example.com").Value,
        MessageBody.Create(ChannelType.Email, "Body text.", "Subject").Value,
        maxAttempts: 3,
        Now).Value;

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public List<string> Published { get; } = [];

        public Task PublishAsync(string type, string payload, CancellationToken cancellationToken)
        {
            Published.Add(payload);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPublisher(string reason) : IOutboxPublisher
    {
        public Task PublishAsync(string type, string payload, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(reason);
    }

    private sealed class SelectivelyFailingPublisher(string failWhenPayloadContains) : IOutboxPublisher
    {
        public Task PublishAsync(string type, string payload, CancellationToken cancellationToken) =>
            payload.Contains(failWhenPayloadContains, StringComparison.Ordinal)
                ? throw new InvalidOperationException("this one fails")
                : Task.CompletedTask;
    }
}
