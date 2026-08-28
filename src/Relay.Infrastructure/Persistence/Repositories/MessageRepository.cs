using Microsoft.EntityFrameworkCore;
using Relay.Domain.Messaging;

namespace Relay.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMessageRepository"/>.
/// </summary>
/// <remarks>
/// Every query here is tracked. That is unusual — read paths normally use
/// <c>AsNoTracking</c> — but nothing in this interface is a read path: every
/// method exists to load a message that is about to be changed, and an untracked
/// aggregate cannot be saved or have its events drained.
/// <para>
/// Read models for the API are built by projecting straight to DTOs elsewhere,
/// which is both faster and unambiguous about intent.
/// </para>
/// </remarks>
internal sealed class MessageRepository(RelayDbContext context) : IMessageRepository
{
    public async Task<Message?> FindAsync(MessageId id, CancellationToken cancellationToken) =>
        await context.Messages
            .Include(m => m.Attempts)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            .ConfigureAwait(false);

    public async Task<Message?> FindByIdempotencyKeyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken) =>
        await context.Messages
            .Include(m => m.Attempts)
            .FirstOrDefaultAsync(m => m.IdempotencyKey == key, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// <c>FOR UPDATE SKIP LOCKED</c>, written as raw SQL because EF Core has no
    /// way to express it.
    /// <para>
    /// This is what lets several workers drain one table concurrently. Each
    /// worker locks the rows it takes; <c>SKIP LOCKED</c> makes the others step
    /// over those rows instead of blocking on them. Without it, the choice is
    /// between workers serialising behind each other — one worker's throughput,
    /// however many are running — and two workers claiming the same message and
    /// both sending it.
    /// </para>
    /// <para>
    /// The optimistic token on the aggregate still applies and is not redundant:
    /// this lock protects the claim, and <c>xmin</c> protects everything that
    /// happens between the claim and the commit, including the case where a worker
    /// is paused long enough for its lock to be irrelevant.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Message>> ClaimPendingAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Deliberately two round trips.
        //
        // The obvious single-query version — FromSql(...).Include(...) — does not
        // work. EF composes over raw SQL by wrapping it in a subquery, and
        // PostgreSQL does not permit FOR UPDATE inside one, so the locking clause
        // either fails or is silently lost depending on how it is composed. Losing
        // it silently is the dangerous outcome: the query still returns rows, and
        // two workers start sending the same message.
        //
        // Claiming the ids first keeps the lock on a query nothing composes over,
        // and the second load is by primary key.
        List<Guid> claimed = await context.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id FROM messages
                 WHERE status = 'Pending'
                 ORDER BY created_at
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return [];
        }

        List<Message> messages = await context.Messages
            .Where(m => claimed.Contains(m.Id.Value))
            .Include(m => m.Attempts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Restored to the order the lock granted them, which is submission order.
        // The second query's ordering is whatever the index returns, and a backlog
        // draining out of order starves whatever arrived first.
        return [.. messages.OrderBy(m => claimed.IndexOf(m.Id.Value))];
    }

    public async Task<IReadOnlyList<Message>> FindAwaitingReceiptAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken) =>
        await context.Messages
            .Where(m => m.Status == MessageStatus.Sent && m.SentAt < olderThan)
            .OrderBy(m => m.SentAt)
            .Take(batchSize)
            .Include(m => m.Attempts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Message>> FindStuckDispatchingAsync(
        DateTimeOffset startedBefore,
        int batchSize,
        CancellationToken cancellationToken) =>
        await context.Messages
            .Where(m => m.Status == MessageStatus.Dispatching && m.DispatchStartedAt < startedBefore)
            .OrderBy(m => m.DispatchStartedAt)
            .Take(batchSize)
            .Include(m => m.Attempts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// Matches the most recent attempt carrying the identifier. A provider that
    /// re-issues one after its retention window has elapsed produces two matching
    /// attempts, and the newer is the one an inbound receipt is about.
    /// </remarks>
    public async Task<Message?> FindByProviderMessageIdAsync(
        ProviderId providerId,
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        MessageId? id = await context.Messages
            .SelectMany(m => m.Attempts, (message, attempt) => new { message.Id, attempt })
            .Where(row =>
                row.attempt.ProviderId == providerId
                && row.attempt.ProviderMessageId == providerMessageId)
            .OrderByDescending(row => row.attempt.AttemptedAt)
            .Select(row => (MessageId?)row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return id is null
            ? null
            : await FindAsync(id.Value, cancellationToken).ConfigureAwait(false);
    }

    public void Add(Message message) => context.Messages.Add(message);
}
