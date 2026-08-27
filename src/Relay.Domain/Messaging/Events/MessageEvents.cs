using Relay.Domain.Common;

namespace Relay.Domain.Messaging.Events;

/// <summary>Raised when a message is accepted and is waiting to be dispatched.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="Channel">The channel it was submitted on.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageQueued(
    MessageId MessageId,
    ChannelType Channel,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a provider accepts a message.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="ProviderId">The provider that accepted it.</param>
/// <param name="ProviderMessageId">The provider's identifier for the message, if it gave one.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageSent(
    MessageId MessageId,
    ProviderId ProviderId,
    string? ProviderMessageId,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when the recipient is confirmed to have received the message.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="ProviderId">The provider that delivered it.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageDelivered(
    MessageId MessageId,
    ProviderId ProviderId,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a provider refuses a message in a way retrying will not fix.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="ProviderId">The provider that refused it.</param>
/// <param name="Reason">Why, verbatim from the provider where available.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageFailed(
    MessageId MessageId,
    ProviderId ProviderId,
    string? Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// Raised when the retry budget is spent without a definite outcome.
/// </summary>
/// <remarks>
/// Distinct from <see cref="MessageFailed"/> on purpose. Failed means somebody
/// said no; dead-lettered means nobody ever said anything. They need different
/// operational responses, so they are different events rather than one event with
/// a flag.
/// </remarks>
/// <param name="MessageId">The message.</param>
/// <param name="Attempts">How many attempts were made.</param>
/// <param name="Reason">The last failure recorded, if any.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageDeadLettered(
    MessageId MessageId,
    int Attempts,
    string? Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a caller withdraws a message before it was dispatched.</summary>
/// <param name="MessageId">The message.</param>
/// <param name="OccurredAt">When this happened, in UTC.</param>
public sealed record MessageCancelled(
    MessageId MessageId,
    DateTimeOffset OccurredAt) : IDomainEvent;
