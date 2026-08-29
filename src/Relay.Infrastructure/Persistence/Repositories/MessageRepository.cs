using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    public async Task<IReadOnlyList<ClaimedMessage>> ClaimPendingAsync(
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

        // Projected to MessageId before the Contains, not compared against
        // m.Id.Value. MessageId reaches the database through a value converter, so
        // EF has no translation for reaching *inside* it — `claimed.Contains(m.Id.Value)`
        // fails at runtime with "the LINQ expression could not be translated",
        // which is a message that points at the query rather than at the converter
        // that caused it. Comparing whole converted values is what EF can do.
        List<MessageId> ids = [.. claimed.Select(id => new MessageId(id))];

        List<Message> messages = await context.Messages
            .Where(m => ids.Contains(m.Id))
            .Include(m => m.Attempts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Restored to the order the lock granted them, which is submission order.
        // The second query's ordering is whatever the index returns, and a backlog
        // draining out of order starves whatever arrived first.
        return
        [
            .. messages
                .OrderBy(m => ids.IndexOf(m.Id))
                .Select(m => new ClaimedMessage(m, TraceParentOf(m))),
        ];
    }

    /// <summary>
    /// Reads the trace context recorded when the message was submitted.
    /// </summary>
    /// <remarks>
    /// A shadow property, so the aggregate never sees it. Read from the change
    /// tracker rather than re-queried — the entity is already loaded and tracked,
    /// so the value is in memory.
    /// </remarks>
    private string? TraceParentOf(Message message) =>
        context.Entry(message).Property<string?>(TraceParentProperty).CurrentValue;

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

    /// <summary>
    /// The shadow property carrying the submission's trace context.
    /// </summary>
    /// <remarks>
    /// Named here and in the entity configuration. A string used in two places is
    /// a rename waiting to break one of them silently — EF resolves shadow
    /// properties by name at runtime, so a mismatch is not a compile error.
    /// </remarks>
    internal const string TraceParentProperty = "submission_trace_parent";

    /// <inheritdoc />
    /// <remarks>
    /// Captures the ambient trace context onto the message as it is added, so a
    /// worker dispatching it hours later can link back to the request that
    /// created it (ADR 0014).
    /// <para>
    /// Done here rather than in the handler because it is a persistence concern:
    /// the application layer should not know that tracing metadata is stored, and
    /// the domain must not.
    /// </para>
    /// </remarks>
    public void Add(Message message)
    {
        EntityEntry<Message> entry = context.Messages.Add(message);

        entry.Property<string?>(TraceParentProperty).CurrentValue = Activity.Current?.Id;
    }
}
