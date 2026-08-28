using Microsoft.Extensions.Logging;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Delivery;

/// <summary>
/// Returns messages abandoned mid-dispatch to the queue.
/// </summary>
/// <remarks>
/// A worker that dies between claiming a message and recording an outcome leaves
/// it in <see cref="MessageStatus.Dispatching"/>. Nothing else will ever move it:
/// the worker that owned it is gone, no receipt is coming for a message that may
/// never have been sent, and the dispatch loop only looks at
/// <see cref="MessageStatus.Pending"/>. Without this loop those messages are lost
/// silently, which is the worst of the available failure modes.
/// <para>
/// Releasing risks a duplicate send — the message may have reached the provider
/// before the worker died. That is the same trade as retrying a timeout, made for
/// the same reason: a possible duplicate is recoverable, a silent loss is not
/// (ADR 0008).
/// </para>
/// </remarks>
public sealed class StuckDispatchRecovery(
    IMessageRepository messages,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<StuckDispatchRecovery> logger)
{
    /// <summary>
    /// Releases messages that have been dispatching longer than any call could take.
    /// </summary>
    /// <param name="stuckAfter">
    /// How long a dispatch may legitimately run. Must exceed the longest provider
    /// timeout plus the resilience chain's retries — a value below that would
    /// release messages out from under workers that are still working on them, and
    /// two workers on one message is the exact outcome this is meant to prevent.
    /// </param>
    /// <param name="batchSize">Most messages to release in one pass.</param>
    /// <param name="cancellationToken">Abandons the pass.</param>
    /// <returns>How many messages were released.</returns>
    public async Task<int> RecoverAsync(
        TimeSpan stuckAfter,
        int batchSize,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();

        IReadOnlyList<Message> stuck = await messages
            .FindStuckDispatchingAsync(now - stuckAfter, batchSize, cancellationToken)
            .ConfigureAwait(false);

        if (stuck.Count == 0)
        {
            return 0;
        }

        int released = 0;

        foreach (Message message in stuck)
        {
            // Read before the transition, which clears it on the way back to
            // Pending. Logging it afterwards would report null for every message.
            DateTimeOffset? startedAt = message.DispatchStartedAt;

            Result outcome = message.ReleaseStuckDispatch(now);

            if (outcome.IsFailure)
            {
                continue;
            }

            released++;

            logger.LogWarning(
                "Message {MessageId} was released after dispatching since {Since}; "
                + "its worker stopped without recording an outcome. It is now {Status}.",
                message.Id.Value,
                startedAt,
                message.Status);
        }

        if (released > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return released;
    }
}
