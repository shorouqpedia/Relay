namespace Relay.Domain.Messaging;

/// <summary>
/// Access to messages, expressed as the questions the domain actually asks.
/// </summary>
/// <remarks>
/// Declared here, in Domain, rather than in Application. The aggregate and the
/// ways it is fetched are one idea — a repository is part of the boundary
/// definition, not a service the use cases happen to need — and putting the port
/// beside the type it loads is what makes the dependency arrow point inward
/// without a second thought.
/// <para>
/// Deliberately not <c>IRepository&lt;T&gt;</c>, and deliberately not returning
/// <c>IQueryable</c>. A generic repository pushes query construction out to the
/// callers, so the shape of the queries a system runs stops being visible in one
/// place and starts being an emergent property of dozens of LINQ expressions —
/// including the ones that accidentally do not translate. Named methods keep the
/// query intent inside the persistence layer, where it can be indexed for.
/// </para>
/// <para>
/// One repository, because there is one aggregate root here.
/// <see cref="DeliveryAttempt"/> has none: it is inside the boundary and is
/// never loaded or saved on its own.
/// </para>
/// </remarks>
public interface IMessageRepository
{
    /// <summary>Loads a message with its delivery history.</summary>
    Task<Message?> FindAsync(MessageId id, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the message a given idempotency key already produced, if any.
    /// </summary>
    /// <remarks>
    /// This is the read half of duplicate suppression, and it is only ever an
    /// optimisation: it answers quickly in the common case where a caller retries
    /// long after the original succeeded. The guarantee itself is the unique index
    /// (ADR 0008) — two concurrent submissions can both find nothing here, and one
    /// of them still loses at the constraint.
    /// </remarks>
    Task<Message?> FindByIdempotencyKeyAsync(IdempotencyKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> messages that are ready to be dispatched.
    /// </summary>
    /// <remarks>
    /// Rows are locked and skipped rather than queued in memory, so several
    /// workers can drain the same table without handing the same message to two of
    /// them. Ordered oldest first, so a backlog drains in submission order rather
    /// than starving whatever arrived during the incident.
    /// </remarks>
    Task<IReadOnlyList<ClaimedMessage>> ClaimPendingAsync(
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds messages a provider accepted but never reported on, past their receipt window.
    /// </summary>
    /// <remarks>
    /// The sweeper's input. This is the failure mode with no event to react to, so
    /// it has to be polled for — silence cannot be subscribed to.
    /// </remarks>
    Task<IReadOnlyList<Message>> FindAwaitingReceiptAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds messages left mid-dispatch by a worker that stopped.
    /// </summary>
    /// <remarks>
    /// Nothing else will ever move these: the worker that claimed them is gone, and
    /// no receipt is coming for a message that may never have been sent. Without
    /// this query they sit in <see cref="MessageStatus.Dispatching"/> permanently.
    /// </remarks>
    Task<IReadOnlyList<Message>> FindStuckDispatchingAsync(
        DateTimeOffset startedBefore,
        int batchSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the message an inbound delivery receipt refers to.
    /// </summary>
    /// <remarks>
    /// Matched on the identifier the provider returned when it accepted the
    /// message. A provider that accepts messages without returning one cannot be
    /// reconciled at all, which is why the provider contract treats returning an
    /// id as a declared capability rather than a nicety.
    /// </remarks>
    Task<Message?> FindByProviderMessageIdAsync(
        ProviderId providerId,
        string providerMessageId,
        CancellationToken cancellationToken);

    /// <summary>Adds a newly submitted message.</summary>
    void Add(Message message);
}

/// <summary>
/// A message claimed for dispatch, with the trace it was submitted under.
/// </summary>
/// <remarks>
/// The trace context is carried alongside the aggregate rather than on it. It is
/// metadata about how the message came to exist, not part of what a message is —
/// the domain has no opinion about distributed tracing and would gain nothing
/// from being able to see it (ADR 0014).
/// <para>
/// A plain string rather than a typed context, so Domain stays free of a
/// dependency on the diagnostics libraries. The worker parses it.
/// </para>
/// </remarks>
/// <param name="Message">The claimed message.</param>
/// <param name="SubmissionTraceParent">
/// The W3C <c>traceparent</c> from the request that submitted it, or
/// <see langword="null"/> when there was none to record.
/// </param>
public sealed record ClaimedMessage(Message Message, string? SubmissionTraceParent);
