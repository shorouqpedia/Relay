using Relay.Domain.Common;

namespace Relay.Domain.Messaging;

/// <summary>
/// Every expected failure the messaging domain can produce.
/// </summary>
/// <remarks>
/// Collected in one place rather than constructed inline so that the full error
/// vocabulary of the domain is one file long and reviewable. The API's mapping to
/// <c>ProblemDetails</c> is written against this list, so an error added here and
/// not mapped there is caught at the boundary rather than in production.
/// </remarks>
public static class MessageErrors
{
    /// <summary>The message does not exist.</summary>
    public static Error NotFound(MessageId id) => Error.NotFound(
        "message.not_found",
        $"No message exists with id '{id}'.");

    /// <summary>A state transition was attempted that the lifecycle does not allow.</summary>
    /// <remarks>
    /// This is a <see cref="ErrorType.Conflict"/> rather than a validation failure
    /// because the request was well-formed — it simply arrived too late, or twice.
    /// A caller retrying a cancellation on an already-delivered message gets this,
    /// and it is the correct answer.
    /// </remarks>
    public static Error InvalidTransition(MessageStatus from, MessageStatus to) => Error.Conflict(
        "message.invalid_transition",
        $"A message cannot move from {from} to {to}.");

    /// <summary>Delivery was attempted after the retry budget was spent.</summary>
    public static Error AttemptsExhausted(int maxAttempts) => Error.Exhausted(
        "message.attempts_exhausted",
        $"The message has already used its {maxAttempts} delivery attempts.");

    /// <summary>No provider is registered and healthy for the requested channel.</summary>
    public static readonly Error NoEligibleProvider = Error.Unavailable(
        "message.no_eligible_provider",
        "No provider is currently available for this channel.");

    /// <summary>The recipient is not valid for the channel it was submitted on.</summary>
    public static Error RecipientNotValidForChannel(ChannelType channel) => Error.Validation(
        "message.recipient_invalid_for_channel",
        $"The recipient is not a valid address for the {channel} channel.");

    /// <summary>The recipient address was absent or malformed.</summary>
    public static readonly Error RecipientMissing = Error.Validation(
        "message.recipient_missing",
        "A recipient is required.");

    /// <summary>The body was empty.</summary>
    public static readonly Error BodyEmpty = Error.Validation(
        "message.body_empty",
        "A message body is required.");

    /// <summary>The body exceeded what the channel can carry.</summary>
    public static Error BodyTooLong(ChannelType channel, int limit, int actual) => Error.Validation(
        "message.body_too_long",
        $"The {channel} channel accepts at most {limit} characters; the body was {actual}.");

    /// <summary>A subject was supplied on a channel that has no notion of one, or omitted where required.</summary>
    public static Error SubjectNotValidForChannel(ChannelType channel) => Error.Validation(
        "message.subject_invalid_for_channel",
        $"The {channel} channel does not support a subject.");

    /// <summary>The idempotency key was absent or malformed.</summary>
    public static readonly Error IdempotencyKeyInvalid = Error.Validation(
        "message.idempotency_key_invalid",
        "An idempotency key is required and must be between 8 and 128 characters.");

    /// <summary>A channel was not supplied, or was the unset default.</summary>
    public static readonly Error ChannelMissing = Error.Validation(
        "message.channel_missing",
        "A channel is required.");

    /// <summary>A provider identifier was absent or malformed.</summary>
    public static readonly Error ProviderIdInvalid = Error.Validation(
        "provider.id_invalid",
        "A provider id is required and must be a lowercase slug.");
}
