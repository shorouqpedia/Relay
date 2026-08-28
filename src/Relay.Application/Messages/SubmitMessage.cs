using FluentValidation;
using Microsoft.Extensions.Logging;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.Messages;

/// <summary>A request to deliver a message.</summary>
/// <param name="Channel">How it should be delivered.</param>
/// <param name="Recipient">Where it is going.</param>
/// <param name="Body">What to send.</param>
/// <param name="Subject">The subject line, on channels that have one.</param>
/// <param name="IdempotencyKey">
/// The caller's token for this submission. Optional; when absent one is derived
/// from the request so a retry of an identical request still deduplicates.
/// </param>
/// <param name="MaxAttempts">How many delivery attempts to allow.</param>
public sealed record SubmitMessageCommand(
    ChannelType Channel,
    string Recipient,
    string Body,
    string? Subject,
    string? IdempotencyKey,
    int? MaxAttempts);

/// <summary>What the caller gets back.</summary>
/// <param name="MessageId">The accepted message.</param>
/// <param name="Status">Where it is in its lifecycle.</param>
/// <param name="WasDuplicate">
/// Whether this submission matched one already accepted. The caller usually does
/// not care — the outcome is the same either way — but it is the difference
/// between a 201 and a 200, and it makes a retry loop visible in a client's own
/// logs.
/// </param>
public sealed record SubmitMessageResult(MessageId MessageId, MessageStatus Status, bool WasDuplicate);

/// <summary>
/// Accepts a message for delivery.
/// </summary>
/// <remarks>
/// The write path where ADR 0008's guarantee is delivered. Duplicate suppression
/// happens twice here, deliberately:
/// <list type="number">
/// <item>a lookup, which answers cheaply in the common case — a caller retrying
/// minutes later after a timeout;</item>
/// <item>the unique index, which answers correctly in the case the lookup cannot
/// — two submissions racing, both finding nothing, both inserting.</item>
/// </list>
/// The first is an optimisation and the second is the guarantee. Writing only the
/// lookup is the mistake this design exists to avoid: it passes every test that
/// does not run concurrently.
/// </remarks>
public sealed class SubmitMessageHandler(
    IMessageRepository messages,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<SubmitMessageHandler> logger)
{
    /// <summary>Attempts to accept the message.</summary>
    public async Task<Result<SubmitMessageResult>> HandleAsync(
        SubmitMessageCommand command,
        CancellationToken cancellationToken)
    {
        Result<IdempotencyKey> key = IdempotencyKey.Create(
            command.IdempotencyKey ?? DeriveKey(command));

        if (key.IsFailure)
        {
            return Result<SubmitMessageResult>.Failure(key.Error);
        }

        Message? existing = await messages
            .FindByIdempotencyKeyAsync(key.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            // The original outcome, not a conflict. A caller retrying after a
            // timeout wants to know what happened to their message, and answering
            // "you already asked" makes them handle an error for something that
            // worked.
            return new SubmitMessageResult(existing.Id, existing.Status, WasDuplicate: true);
        }

        Result<Recipient> recipient = Recipient.Create(command.Channel, command.Recipient);
        Result<MessageBody> body = MessageBody.Create(command.Channel, command.Body, command.Subject);

        if (Result.FirstFailure(ToResult(recipient), ToResult(body)) is { IsFailure: true } invalid)
        {
            return Result<SubmitMessageResult>.Failure(invalid.Error);
        }

        Result<Message> message = Message.Submit(
            key.Value,
            recipient.Value,
            body.Value,
            command.MaxAttempts ?? DefaultMaxAttempts,
            clock.GetUtcNow());

        if (message.IsFailure)
        {
            return Result<SubmitMessageResult>.Failure(message.Error);
        }

        messages.Add(message.Value);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DuplicateIdempotencyKeyException)
        {
            // The lookup above found nothing and the insert lost anyway — another
            // submission with the same key committed in between. This is the case
            // the index exists for, and it is a normal outcome under concurrency
            // rather than an error: the caller gets the same answer they would
            // have got had they arrived a moment later.
            logger.LogInformation(
                "A concurrent submission already claimed idempotency key {Key}; "
                + "returning the message it created.",
                key.Value.Value);

            Message? winner = await messages
                .FindByIdempotencyKeyAsync(key.Value, cancellationToken)
                .ConfigureAwait(false);

            return winner is null
                ? Result<SubmitMessageResult>.Failure(Error.Failure(
                    "message.submit_conflict",
                    "A concurrent submission claimed this idempotency key but could not be read back."))
                : new SubmitMessageResult(winner.Id, winner.Status, WasDuplicate: true);
        }

        return new SubmitMessageResult(message.Value.Id, message.Value.Status, WasDuplicate: false);
    }

    /// <summary>
    /// How many attempts a message gets when the caller does not say.
    /// </summary>
    /// <remarks>
    /// Three, because the budget is spent on failover between providers rather
    /// than on repeating one — the resilience decorator already retries within a
    /// single attempt. More than that and a message with a genuinely bad recipient
    /// takes a long time to give up.
    /// </remarks>
    private const int DefaultMaxAttempts = 3;

    /// <summary>
    /// Derives a key from the request when the caller supplies none.
    /// </summary>
    /// <remarks>
    /// Content-derived, so that two identical submissions deduplicate even from a
    /// caller that does not participate in idempotency at all. This is weaker than
    /// a caller-supplied key — two genuinely distinct messages with the same
    /// recipient and body collapse into one — which is why the API documents that
    /// callers should supply their own.
    /// </remarks>
    private static string DeriveKey(SubmitMessageCommand command)
    {
        // Separated by a character that cannot occur in any of the fields, so the
        // concatenation is unambiguous. Without one, a subject ending where a body
        // begins produces the same material as a different split of the same text,
        // and two distinct messages would share a derived key.
        const char separator = (char)0x1F;

        string material = string.Join(
            separator,
            command.Channel,
            command.Recipient,
            command.Subject ?? string.Empty,
            command.Body);

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(material));

        return $"derived-{Convert.ToHexStringLower(hash)[..48]}";
    }

    private static Result ToResult<T>(Result<T> result) =>
        result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
}

/// <summary>Rejects submissions the domain would refuse anyway, before any work is done.</summary>
/// <remarks>
/// The value objects validate the same things and are the real gate — this exists
/// to collect every problem in one response rather than reporting the first one
/// the handler happens to construct. A caller fixing four fields one round trip at
/// a time is a bad API.
/// </remarks>
public sealed class SubmitMessageValidator : AbstractValidator<SubmitMessageCommand>
{
    /// <summary>Configures the rules.</summary>
    public SubmitMessageValidator()
    {
        RuleFor(c => c.Channel)
            .NotEqual(ChannelType.None)
            .WithMessage("A channel is required.");

        RuleFor(c => c.Recipient)
            .NotEmpty()
            .WithMessage("A recipient is required.");

        RuleFor(c => c.Body)
            .NotEmpty()
            .WithMessage("A message body is required.");

        RuleFor(c => c.MaxAttempts)
            .InclusiveBetween(1, 10)
            .When(c => c.MaxAttempts.HasValue)
            .WithMessage("MaxAttempts must be between 1 and 10.");

        RuleFor(c => c.IdempotencyKey)
            .Length(IdempotencyKey.MinLength, IdempotencyKey.MaxLength)
            .When(c => !string.IsNullOrWhiteSpace(c.IdempotencyKey))
            .WithMessage(
                $"An idempotency key must be between {IdempotencyKey.MinLength} "
                + $"and {IdempotencyKey.MaxLength} characters.");
    }
}
