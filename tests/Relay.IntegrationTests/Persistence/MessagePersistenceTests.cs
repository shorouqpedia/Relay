using Microsoft.EntityFrameworkCore;
using Relay.Domain.Common;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Persistence.Repositories;

namespace Relay.IntegrationTests.Persistence;

/// <summary>
/// What the database guarantees, asserted against a real one.
/// </summary>
/// <remarks>
/// These are not tests of the mapping code. They are tests of the properties the
/// design depends on the storage engine to provide, which is why none of them
/// could be written against an in-memory provider.
/// <para>Scenario ids <c>PR01</c>–<c>PR08</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class MessagePersistenceTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PR01_Message_RoundTripsWithItsValueObjectsIntact()
    {
        Message original = NewMessage(Key(), ChannelType.Email, "Someone@Example.COM");

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(original);
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using RelayDbContext read = postgres.CreateContext();
        Message? loaded = await new MessageRepository(read)
            .FindAsync(original.Id, TestContext.Current.CancellationToken);

        loaded.ShouldNotBeNull();

        // The value objects come back as value objects, not as loose strings that
        // happen to round-trip. Address is lowercased because Recipient normalised
        // it on the way in, and nothing since has had the chance to un-normalise it.
        loaded.Recipient.Address.ShouldBe("someone@example.com");
        loaded.Recipient.Channel.ShouldBe(ChannelType.Email);
        loaded.Body.Subject.ShouldBe("Subject");
        loaded.IdempotencyKey.ShouldBe(original.IdempotencyKey);
        loaded.Status.ShouldBe(MessageStatus.Pending);
    }

    [Fact]
    public async Task PR02_DuplicateIdempotencyKey_IsRejectedByTheDatabase()
    {
        string key = Key();

        await using (RelayDbContext first = postgres.CreateContext())
        {
            first.Messages.Add(NewMessage(key, ChannelType.Email, "a@example.com"));
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using RelayDbContext second = postgres.CreateContext();
        second.Messages.Add(NewMessage(key, ChannelType.Email, "b@example.com"));

        // The guarantee behind ADR 0008, and the reason it is an index rather than
        // a lookup. A check-then-insert has a window; this does not.
        DbUpdateException failure = await Should.ThrowAsync<DbUpdateException>(
            async () => await second.SaveChangesAsync(TestContext.Current.CancellationToken));

        failure.InnerException.ShouldNotBeNull();
        failure.InnerException.Message.ShouldContain("ix_messages_idempotency_key");
    }

    [Fact]
    public async Task PR03_IdempotencyKey_MatchesRegardlessOfCasingAndPadding()
    {
        string key = Key();

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(NewMessage(key, ChannelType.Email, "a@example.com"));
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // The caller retries, spelling the key differently. The index compares
        // bytes, so this only finds anything because IdempotencyKey normalised
        // both spellings to the same value before either reached the database.
        IdempotencyKey retried = IdempotencyKey.Create($"  {key.ToUpperInvariant()}  ").Value;

        await using RelayDbContext read = postgres.CreateContext();
        Message? found = await new MessageRepository(read)
            .FindByIdempotencyKeyAsync(retried, TestContext.Current.CancellationToken);

        found.ShouldNotBeNull();
    }

    [Fact]
    public async Task PR04_ConcurrentWriters_TheSecondOneLoses()
    {
        Message message = NewMessage(Key(), ChannelType.Email, "a@example.com");

        await using (RelayDbContext seed = postgres.CreateContext())
        {
            seed.Messages.Add(message);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Two workers load the same message and both decide to dispatch it. Both
        // succeed in memory — the domain has no way to know about the other — and
        // this is the last line of defence before a duplicate send.
        await using RelayDbContext workerOne = postgres.CreateContext();
        await using RelayDbContext workerTwo = postgres.CreateContext();

        Message forOne = (await new MessageRepository(workerOne)
            .FindAsync(message.Id, TestContext.Current.CancellationToken))!;
        Message forTwo = (await new MessageRepository(workerTwo)
            .FindAsync(message.Id, TestContext.Current.CancellationToken))!;

        forOne.BeginDispatch(Provider("email.postal"), Now).IsSuccess.ShouldBeTrue();
        forTwo.BeginDispatch(Provider("email.mailhook"), Now).IsSuccess.ShouldBeTrue();

        await new UnitOfWork(workerOne, TimeProvider.System)
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        // xmin changed under worker two. It stops instead of sending.
        await Should.ThrowAsync<ConcurrencyConflictException>(
            async () => await new UnitOfWork(workerTwo, TimeProvider.System)
                .SaveChangesAsync(TestContext.Current.CancellationToken));

        await using RelayDbContext verify = postgres.CreateContext();
        Message winner = (await new MessageRepository(verify)
            .FindAsync(message.Id, TestContext.Current.CancellationToken))!;

        winner.CurrentProviderId!.Value.ShouldBe("email.postal");
    }

    [Fact]
    public async Task PR05_Attempts_PersistInOrderAndBelongToTheirMessage()
    {
        Message message = NewMessage(Key(), ChannelType.Email, "a@example.com");
        ProviderId provider = Provider("email.postal");

        message.BeginDispatch(provider, Now);
        message.RecordAttempt(AttemptOutcome.TransientFailure, null, "first", TimeSpan.FromMilliseconds(10), Now);
        message.BeginDispatch(provider, Now.AddSeconds(30));
        message.RecordAttempt(AttemptOutcome.Accepted, "pm-1", null, TimeSpan.FromMilliseconds(20), Now.AddSeconds(30));

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(message);
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using RelayDbContext read = postgres.CreateContext();
        Message loaded = (await new MessageRepository(read)
            .FindAsync(message.Id, TestContext.Current.CancellationToken))!;

        loaded.Status.ShouldBe(MessageStatus.Sent);
        loaded.Attempts.Select(a => a.Sequence).ShouldBe([1, 2]);
        loaded.Attempts[1].ProviderMessageId.ShouldBe("pm-1");
    }

    [Fact]
    public async Task PR06_ReceiptLookup_FindsTheMessageByProviderMessageId()
    {
        Message message = NewMessage(Key(), ChannelType.Email, "a@example.com");
        ProviderId provider = Provider("email.postal");
        string upstreamId = $"upstream-{Guid.CreateVersion7():N}";

        message.BeginDispatch(provider, Now);
        message.RecordAttempt(AttemptOutcome.Accepted, upstreamId, null, TimeSpan.Zero, Now);

        await using (RelayDbContext write = postgres.CreateContext())
        {
            write.Messages.Add(message);
            await write.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using RelayDbContext read = postgres.CreateContext();
        Message? found = await new MessageRepository(read).FindByProviderMessageIdAsync(
            provider, upstreamId, TestContext.Current.CancellationToken);

        // This is the path an inbound delivery receipt takes. A provider that
        // returns no identifier has no equivalent, which is why the contract
        // treats returning one as a declared capability.
        found.ShouldNotBeNull();
        found.Id.ShouldBe(message.Id);
    }

    [Fact]
    public async Task PR07_ClaimPending_HandsTwoWorkersDisjointSetsOfMessages()
    {
        List<MessageId> seeded = [];

        await using (RelayDbContext seed = postgres.CreateContext())
        {
            for (int i = 0; i < 6; i++)
            {
                Message message = NewMessage(Key(), ChannelType.Email, $"claim{i}@example.com");
                seed.Messages.Add(message);
                seeded.Add(message.Id);
            }

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Two workers claim concurrently, each inside its own transaction. Without
        // SKIP LOCKED one of them either blocks until the other commits — one
        // worker's throughput no matter how many are running — or reads the same
        // rows and sends everything twice.
        await using RelayDbContext workerOne = postgres.CreateContext();
        await using RelayDbContext workerTwo = postgres.CreateContext();

        await using var txOne = await workerOne.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var txTwo = await workerTwo.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<Message> claimedByOne = await new MessageRepository(workerOne)
            .ClaimPendingAsync(3, TestContext.Current.CancellationToken);
        IReadOnlyList<Message> claimedByTwo = await new MessageRepository(workerTwo)
            .ClaimPendingAsync(3, TestContext.Current.CancellationToken);

        claimedByOne.ShouldNotBeEmpty();
        claimedByTwo.ShouldNotBeEmpty();

        HashSet<MessageId> one = [.. claimedByOne.Select(m => m.Id)];
        HashSet<MessageId> two = [.. claimedByTwo.Select(m => m.Id)];

        one.Overlaps(two).ShouldBeFalse();

        await txOne.RollbackAsync(TestContext.Current.CancellationToken);
        await txTwo.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PR08_ClaimPending_ReturnsOldestFirst()
    {
        List<MessageId> inOrder = [];

        await using (RelayDbContext seed = postgres.CreateContext())
        {
            // Deliberately inserted newest first, so passing requires the ordering
            // to come from created_at rather than from insertion order.
            for (int i = 2; i >= 0; i--)
            {
                Message message = NewMessage(
                    Key(),
                    ChannelType.Sms,
                    "+201001234567",
                    createdAt: Now.AddMinutes(i));

                seed.Messages.Add(message);
                inOrder.Insert(0, message.Id);
            }

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using RelayDbContext worker = postgres.CreateContext();
        await using var transaction = await worker.Database
            .BeginTransactionAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<Message> claimed = await new MessageRepository(worker)
            .ClaimPendingAsync(3, TestContext.Current.CancellationToken);

        claimed.Select(m => m.Id).ShouldBe(inOrder);

        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    private static string Key() => $"idem-{Guid.CreateVersion7():N}";

    private static ProviderId Provider(string id) => ProviderId.Create(id).Value;

    private static Message NewMessage(
        string key,
        ChannelType channel,
        string address,
        DateTimeOffset? createdAt = null) =>
        Message.Submit(
            IdempotencyKey.Create(key).Value,
            Recipient.Create(channel, address).Value,
            MessageBody.Create(
                channel,
                "Body text.",
                channel is ChannelType.Email ? "Subject" : null).Value,
            maxAttempts: 3,
            createdAt ?? Now).Value;
}
