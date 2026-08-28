using Relay.Domain.Messaging;

namespace Relay.Application.Messages;

/// <summary>What a caller is told about a message.</summary>
/// <remarks>
/// A projection, not the aggregate. The aggregate is never returned from an
/// endpoint: it carries behaviour, private collections, and a concurrency token,
/// none of which mean anything over HTTP — and serializing it would turn every
/// internal rename into a breaking API change.
/// </remarks>
/// <param name="Id">The message.</param>
/// <param name="Channel">How it is being delivered.</param>
/// <param name="Recipient">Where it is going.</param>
/// <param name="Subject">The subject line, where the channel has one.</param>
/// <param name="Status">Where it is in its lifecycle.</param>
/// <param name="AttemptCount">How many delivery attempts have been made.</param>
/// <param name="MaxAttempts">How many are allowed in total.</param>
/// <param name="FailureReason">Why it failed, when it did.</param>
/// <param name="CreatedAt">When it was accepted.</param>
/// <param name="SentAt">When a provider accepted it.</param>
/// <param name="CompletedAt">When it reached a terminal state.</param>
/// <param name="Attempts">The delivery history, oldest first.</param>
public sealed record MessageView(
    Guid Id,
    string Channel,
    string Recipient,
    string? Subject,
    string Status,
    int AttemptCount,
    int MaxAttempts,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AttemptView> Attempts);

/// <summary>One recorded delivery attempt, as reported to a caller.</summary>
/// <param name="Sequence">Position in the attempt history, starting at one.</param>
/// <param name="Provider">Which provider was tried.</param>
/// <param name="Outcome">How the attempt ended.</param>
/// <param name="FailureReason">Why it failed, verbatim from the provider.</param>
/// <param name="DurationMs">How long the call took.</param>
/// <param name="AttemptedAt">When it was made.</param>
public sealed record AttemptView(
    int Sequence,
    string Provider,
    string Outcome,
    string? FailureReason,
    double DurationMs,
    DateTimeOffset AttemptedAt);

/// <summary>
/// Reads messages for display.
/// </summary>
/// <remarks>
/// Separate from <see cref="IMessageRepository"/> on purpose, and this is where
/// the CQRS split in this codebase actually shows up. The repository loads
/// aggregates to be changed — tracked, with their collections, through their
/// value objects. This projects straight to DTOs, untracked, and never
/// materialises a <see cref="Message"/> at all.
/// <para>
/// Sharing one interface would force one of those to be wrong: either reads pay
/// for change tracking they do not use, or writes get an untracked aggregate they
/// cannot save.
/// </para>
/// </remarks>
public interface IMessageReader
{
    /// <summary>Reads one message, or <see langword="null"/> if it does not exist.</summary>
    Task<MessageView?> FindAsync(MessageId id, CancellationToken cancellationToken);
}
