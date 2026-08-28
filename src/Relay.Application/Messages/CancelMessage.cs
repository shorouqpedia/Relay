using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Messages;

/// <summary>
/// Withdraws a message that has not been handed to a provider yet.
/// </summary>
/// <remarks>
/// The interesting case is the one that fails. Once a provider has the message,
/// Relay cannot unsend it, so cancelling reports a conflict rather than
/// succeeding. Reporting success would be a lie the caller acts on — they would
/// stop expecting the message to arrive, and it would arrive.
/// </remarks>
public sealed class CancelMessageHandler(
    IMessageRepository messages,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
{
    /// <summary>Attempts to cancel the message.</summary>
    public async Task<Result> HandleAsync(MessageId id, CancellationToken cancellationToken)
    {
        Message? message = await messages.FindAsync(id, cancellationToken).ConfigureAwait(false);

        if (message is null)
        {
            return MessageErrors.NotFound(id);
        }

        if (message.Status is MessageStatus.Cancelled)
        {
            // Already cancelled. Succeeds rather than conflicting, for the same
            // reason a duplicate delivery receipt succeeds: the caller asked for a
            // state, the message is in that state, and reporting an error would
            // make a successful retry look like a failure.
            return Result.Success();
        }

        Result cancelled = message.Cancel(clock.GetUtcNow());

        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }
}
