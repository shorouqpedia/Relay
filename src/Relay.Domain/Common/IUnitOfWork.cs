namespace Relay.Domain.Common;

/// <summary>
/// Commits a set of changes, and the events they produced, as one thing.
/// </summary>
/// <remarks>
/// The reason this exists as a port rather than being an implementation detail of
/// the repository is the "and" in that sentence. A handler that marks a message
/// sent and publishes <c>MessageSent</c> has to do both or neither: publishing
/// without committing announces something that did not happen, and committing
/// without publishing loses it silently — and silently is worse, because nothing
/// downstream ever learns there was something to wait for.
/// <para>
/// Implemented over the outbox (ADR 0008): draining each aggregate's events and
/// writing them as rows in the same transaction as the state change. Events reach
/// their subscribers afterwards, from those rows, which is what makes delivery
/// at-least-once rather than best-effort.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits the tracked changes together with the events they raised.
    /// </summary>
    /// <returns>The number of rows written, including outbox rows.</returns>
    /// <exception cref="ConcurrencyConflictException">
    /// Another writer changed one of these aggregates first. Genuinely
    /// exceptional — the caller did nothing wrong, and the answer is to reload and
    /// retry rather than to report a failure to the user.
    /// </exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Thrown when an aggregate was modified by someone else between load and save.
/// </summary>
/// <remarks>
/// An exception rather than a <see cref="Result"/>, and this is the boundary
/// between the two that ADR 0005 draws. A caller cannot act on this: it is not a
/// decision they made, it carries no information they can use, and the correct
/// response — reload and try again — is mechanical. It also has to interrupt
/// whatever was in progress, because everything after it would be operating on
/// stale state.
/// <para>
/// In this system the conflict is nearly always two workers reaching the same
/// message, which is exactly what the optimistic token is there to catch: the
/// loser stops rather than sending a duplicate.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public ConcurrencyConflictException()
        : base("The aggregate was modified by another writer since it was loaded.")
    {
    }

    /// <summary>Creates the exception with a specific message.</summary>
    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the store's own error.</summary>
    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
