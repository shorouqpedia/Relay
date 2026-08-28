using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Relay.Infrastructure.Persistence;

namespace Relay.Infrastructure.Persistence.Outbox;

/// <summary>
/// Publishes what the outbox has accumulated.
/// </summary>
/// <remarks>
/// Runs on a schedule, claims a batch, publishes each row, and marks it
/// processed. Rows that fail stay pending and are attempted again.
/// <para>
/// Consumers must be idempotent, and this is where that requirement comes from: a
/// process that dies between publishing a row and marking it processed will
/// publish it again on the next pass. Making that window smaller does not remove
/// it — there is no ordering of "publish" and "mark" that closes it, only one
/// that chooses which duplicate you get. Publishing twice is recoverable;
/// publishing zero times is not.
/// </para>
/// </remarks>
// Public because a host has to schedule it. The repositories and unit of work
// stay internal — those are resolved through their interfaces — but this has no
// interface to hide behind, and inventing one whose only implementation is this
// class would be indirection for its own sake.
public sealed class OutboxDispatcher(
    RelayDbContext context,
    IOutboxPublisher publisher,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger)
{
    /// <summary>
    /// Publishes one batch.
    /// </summary>
    /// <returns>How many rows were successfully published.</returns>
    public async Task<int> DispatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        // The same FOR UPDATE SKIP LOCKED claim the delivery pipeline uses, and
        // for the same reason: several instances of the dispatcher run in a scaled
        // deployment, and without the lock they would each publish every row.
        List<Guid> claimed = await context.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id FROM outbox_messages
                 WHERE processed_at IS NULL
                 ORDER BY occurred_at
                 LIMIT {batchSize}
                 FOR UPDATE SKIP LOCKED
                 """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return 0;
        }

        List<OutboxMessage> batch = await context.OutboxMessages
            .Where(row => claimed.Contains(row.Id))
            .OrderBy(row => row.OccurredAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        DateTimeOffset now = clock.GetUtcNow();
        int published = 0;

        foreach (OutboxMessage row in batch)
        {
            if (await TryPublishAsync(row, now, cancellationToken).ConfigureAwait(false))
            {
                published++;
            }
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return published;
    }

    private async Task<bool> TryPublishAsync(
        OutboxMessage row,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(row.Type, row.Payload, cancellationToken)
                .ConfigureAwait(false);

            row.MarkProcessed(now);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown. The row stays pending, which is exactly right — it will be
            // picked up by whoever runs next.
            throw;
        }
        catch (Exception exception)
        {
            // One of the few genuinely justified broad catches in this codebase,
            // and it is narrow in scope rather than in type: a single row that
            // cannot be published must not stop the rest of the batch, and the
            // set of things a publisher can throw is not enumerable from here.
            //
            // Nothing is swallowed. The failure is recorded on the row, the row
            // stays pending, and it is retried — the opposite of the catch-all
            // handlers ADR 0009 rules out, which convert a failure into a generic
            // response and lose it.
            row.RecordFailure(Describe(exception));

            logger.LogError(
                exception,
                "Failed to publish outbox row {OutboxId} of type {EventType}. "
                + "Attempt {AttemptCount}; it stays pending and will be retried.",
                row.Id,
                row.Type,
                row.AttemptCount);

            return false;
        }
    }

    private static string Describe(Exception exception)
    {
        string description = $"{exception.GetType().Name}: {exception.Message}";
        return description.Length <= 4_000 ? description : description[..4_000];
    }
}

/// <summary>
/// Sends a serialized domain event onward.
/// </summary>
/// <remarks>
/// An interface rather than a concrete broker client so that the dispatcher's
/// claim-publish-mark loop can be tested without a broker, and so that swapping
/// the transport does not touch the part of this that is actually subtle.
/// </remarks>
public interface IOutboxPublisher
{
    /// <summary>Publishes one event.</summary>
    /// <param name="type">The event's type name, as recorded on the row.</param>
    /// <param name="payload">The serialized event.</param>
    /// <param name="cancellationToken">Abandons the publish.</param>
    Task PublishAsync(string type, string payload, CancellationToken cancellationToken);
}
