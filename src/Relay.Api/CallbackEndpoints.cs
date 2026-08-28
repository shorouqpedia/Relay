using Microsoft.AspNetCore.Http.HttpResults;
using Relay.Application.Callbacks;

namespace Relay.Api;

/// <summary>
/// The endpoint providers call to report delivery.
/// </summary>
/// <remarks>
/// The only place an outside party writes to Relay's state, and the only
/// unauthenticated endpoint in the system. Everything about how it is written
/// follows from that — see ADR 0013.
/// </remarks>
internal static class CallbackEndpoints
{
    /// <summary>
    /// Most a callback body may be.
    /// </summary>
    /// <remarks>
    /// The body is buffered whole in order to verify a signature over it, so an
    /// unbounded endpoint is an unbounded allocation that anyone on the internet
    /// can trigger. A megabyte is far beyond the batching providers, which send
    /// tens of receipts.
    /// </remarks>
    private const long MaxBodyBytes = 1_024 * 1_024;

    /// <summary>Maps the callback endpoint.</summary>
    public static void MapCallbacks(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder callbacks = app
            .MapGroup("/api/v{version:apiVersion}/callbacks")
            .WithTags("Callbacks");

        callbacks
            .MapPost("/{providerId}", ReceiveAsync)
            .WithName("ReceiveCallback")
            .WithSummary("Receives a delivery receipt from a provider.")
            .WithDescription(
                "Public and unauthenticated in the usual sense: providers cannot "
                + "hold a bearer token, so trust comes from an HMAC signature over "
                + "the raw body plus a timestamp. A well-formed callback is "
                + "acknowledged with 204 even when it changes nothing — a duplicate "
                + "receipt is normal, and answering 4xx would make the provider "
                + "resend it.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)

            // No response caching, and no authorization filter to bypass: this
            // group deliberately carries neither the validation filter nor
            // anything else that would parse the body before it is verified.
            .DisableAntiforgery();
    }

    /// <summary>Verifies a callback and applies what it reports.</summary>
    /// <remarks>
    /// Takes <see cref="HttpContext"/> rather than a bound model, which is the
    /// point. A signature covers the bytes that were sent; binding to a model and
    /// re-serializing produces different bytes and a signature that can never
    /// match. So the body is read raw, and nothing interprets it until the
    /// provider's verifier has approved it.
    /// </remarks>
    private static async Task<Results<NoContent, ProblemHttpResult>> ReceiveAsync(
        string providerId,
        HttpContext context,
        ProcessCallbackHandler handler,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return TypedResults.Problem(
                title: "The callback body is too large.",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        context.Request.EnableBuffering();

        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var submission = new CallbackSubmission(
            providerId,
            context.Request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToString(),

                // Providers are inconsistent about header casing, and HTTP says it
                // does not matter. A case-sensitive lookup here would reject valid
                // callbacks from a provider that changed its client library.
                StringComparer.OrdinalIgnoreCase),
            body);

        CallbackResult result = await handler
            .HandleAsync(submission, cancellationToken)
            .ConfigureAwait(false);

        return result.Decision switch
        {
            // 204 for anything verified, including a callback that moved nothing.
            // The status answers "did you receive this?" — a provider reading a
            // 4xx resends, so a conflict response for a duplicate causes the
            // retries it looks like it is reporting.
            CallbackDecision.Acknowledge => TypedResults.NoContent(),

            // No detail, on purpose. Telling a caller whether the signature was
            // wrong, the timestamp stale, or the provider unknown helps an attacker
            // more than it helps a misconfigured provider — who finds the answer in
            // Relay's own logs instead (ADR 0013).
            CallbackDecision.Refuse => TypedResults.Problem(
                title: "The callback could not be verified.",
                statusCode: StatusCodes.Status401Unauthorized),

            // Distinguished from a refusal because it means something different: a
            // correctly signed payload Relay could not read is a provider that
            // changed its format, which is a bug to fix rather than an intrusion
            // to ignore.
            CallbackDecision.Unreadable => TypedResults.Problem(
                title: "The callback was signed correctly but could not be read.",
                statusCode: StatusCodes.Status422UnprocessableEntity),

            CallbackDecision.UnknownProvider => TypedResults.Problem(
                title: "No such provider.",
                statusCode: StatusCodes.Status404NotFound),

            _ => TypedResults.Problem(
                title: "The callback could not be processed.",
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }
}
