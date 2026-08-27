using System.Text.Json.Serialization;
using Refit;

namespace Relay.Providers.Email.Postal;

/// <summary>
/// The subset of the Postal API that Relay uses.
/// </summary>
/// <remarks>
/// Every method returns <see cref="IApiResponse{T}"/> rather than the payload
/// directly. Refit's default is to throw <c>ApiException</c> on a non-success
/// status, which would turn an ordinary 429 into an exception the provider then
/// has to catch and convert back — and a provider whose normal control flow runs
/// through a catch block will eventually let one escape.
/// </remarks>
internal interface IPostalApi
{
    /// <summary>Submits one message for delivery.</summary>
    [Post("/v1/messages")]
    Task<IApiResponse<PostalSendResponse>> SendAsync(
        [Body] PostalSendRequest request,
        CancellationToken cancellationToken);

    /// <summary>Asks what became of a message Postal accepted.</summary>
    [Get("/v1/messages/{id}")]
    Task<IApiResponse<PostalMessageStatusResponse>> GetStatusAsync(
        string id,
        CancellationToken cancellationToken);
}

/// <summary>A message submission, in Postal's shape.</summary>
internal sealed record PostalSendRequest
{
    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>
    /// Postal's own duplicate-suppression key.
    /// </summary>
    /// <remarks>
    /// Passed through as an optimisation only — it saves an upstream send on a
    /// retry. Relay's guarantee does not depend on it (ADR 0008), because Postal's
    /// key retention window is shorter than Relay's retry budget can be.
    /// </remarks>
    [JsonPropertyName("idempotency_key")]
    public required string IdempotencyKey { get; init; }
}

/// <summary>Postal's answer to an accepted submission.</summary>
internal sealed record PostalSendResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

/// <summary>Postal's answer to a status query.</summary>
internal sealed record PostalMessageStatusResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

/// <summary>Postal's error body, returned alongside a non-success status.</summary>
internal sealed record PostalErrorResponse
{
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
