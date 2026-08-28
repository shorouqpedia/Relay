using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Relay.Application.Messages;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Api;

/// <summary>
/// The message resource.
/// </summary>
/// <remarks>
/// Named static methods, not lambdas inline in <c>Program.cs</c>. Lambdas are how
/// minimal APIs earn a reputation for being unstructured, and the reputation is
/// deserved once everything lives in one file: the handler has no name to appear
/// in a stack trace, no place to hang documentation, and no way to be read
/// without the routing around it.
/// </remarks>
internal static class MessageEndpoints
{
    /// <summary>Maps the message endpoints onto a versioned group.</summary>
    public static void MapMessages(this IEndpointRouteBuilder app)
    {
        // Everything that runs before these endpoints is listed here, in one
        // place. That is the property ADR 0012 trades a mediator for.
        RouteGroupBuilder messages = app
            .MapGroup("/api/v{version:apiVersion}/messages")
            .WithTags("Messages");

        messages
            .MapPost("/", SubmitAsync)
            .AddEndpointFilter<ValidationFilter<SubmitMessageCommand>>()
            .WithName("SubmitMessage")
            .WithSummary("Submits a message for delivery.")
            .WithDescription(
                "Idempotent. Supply an Idempotency-Key so a retry after a timeout "
                + "returns the original message rather than sending a second one. "
                + "Without one, a key is derived from the request body, which "
                + "deduplicates identical submissions but cannot distinguish two "
                + "genuinely separate messages with the same content.")
            .Produces<SubmitMessageResponse>(StatusCodes.Status201Created)
            .Produces<SubmitMessageResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        messages
            .MapGet("/{id:guid}", GetAsync)
            .WithName("GetMessage")
            .WithSummary("Reads a message and its delivery history.")
            .Produces<MessageView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        messages
            .MapPost("/{id:guid}/cancel", CancelAsync)
            .WithName("CancelMessage")
            .WithSummary("Withdraws a message that has not been dispatched yet.")
            .WithDescription(
                "Succeeds only while the message is still pending. Once a provider "
                + "has it, Relay cannot unsend it, and this returns 409 — reporting "
                + "success would tell the caller to stop expecting a message that "
                + "is going to arrive.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    /// <summary>Accepts a message for delivery.</summary>
    private static async Task<Results<Created<SubmitMessageResponse>, Ok<SubmitMessageResponse>, ProblemHttpResult>>
        SubmitAsync(
            SubmitMessageCommand command,
            SubmitMessageHandler handler,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            CancellationToken cancellationToken)
    {
        // The header wins over the body field. A proxy or client library that sets
        // the header is expressing the same intent, and having two sources with no
        // stated precedence is how one silently stops working.
        SubmitMessageCommand withKey = string.IsNullOrWhiteSpace(idempotencyKey)
            ? command
            : command with { IdempotencyKey = idempotencyKey };

        Result<SubmitMessageResult> result = await handler
            .HandleAsync(withKey, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var response = new SubmitMessageResponse(
            result.Value.MessageId.Value,
            result.Value.Status.ToString());

        // 200 for a duplicate, 201 for a new message. Both carry the same body,
        // because the caller's question — what happened to my message — has the
        // same answer either way. The status is what tells a client whether its
        // retry was the one that landed.
        return result.Value.WasDuplicate
            ? TypedResults.Ok(response)
            : TypedResults.Created($"/api/v1/messages/{result.Value.MessageId.Value}", response);
    }

    /// <summary>Reads a message and its delivery history.</summary>
    private static async Task<Results<Ok<MessageView>, ProblemHttpResult>> GetAsync(
        Guid id,
        IMessageReader reader,
        CancellationToken cancellationToken)
    {
        MessageView? message = await reader
            .FindAsync(new MessageId(id), cancellationToken)
            .ConfigureAwait(false);

        return message is null
            ? MessageErrors.NotFound(new MessageId(id)).ToProblem()
            : TypedResults.Ok(message);
    }

    /// <summary>Withdraws a message that has not been dispatched.</summary>
    private static async Task<Results<NoContent, ProblemHttpResult>> CancelAsync(
        Guid id,
        CancelMessageHandler handler,
        CancellationToken cancellationToken)
    {
        Result result = await handler
            .HandleAsync(new MessageId(id), cancellationToken)
            .ConfigureAwait(false);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : result.Error.ToProblem();
    }
}

/// <summary>What a caller gets back from a submission.</summary>
/// <param name="Id">The accepted message.</param>
/// <param name="Status">Where it is in its lifecycle.</param>
public sealed record SubmitMessageResponse(Guid Id, string Status);
