using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Webhook;

/// <summary>
/// Delivers by calling an endpoint the recipient supplied.
/// </summary>
/// <remarks>
/// The provider that is least like the others, and the reason the contract is
/// worth having. There is no upstream vendor here at all: the recipient <i>is</i>
/// the destination, so every message goes to a different host, chosen by whoever
/// submitted it.
/// <para>
/// That inverts most of the assumptions the other providers rest on. There is no
/// account, no API key, no rate limit to respect, no message id to reconcile
/// against, and no shared connection pool warmed by previous sends. What there is
/// instead is a signature, because the receiver has no other way to know the
/// request came from Relay.
/// </para>
/// <para>
/// It also has the weakest capability set in the system: it cannot return a
/// provider message id, so a message delivered this way can never be reconciled.
/// The 2xx is the only evidence that will ever exist. Declaring that honestly is
/// what lets the sweeper abandon such messages with an accurate reason rather
/// than waiting for a receipt nobody is going to send.
/// </para>
/// </remarks>
internal sealed class WebhookProvider(HttpClient client, IOptions<WebhookOptions> options)
    : IMessageProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ProviderDescriptor Self = new(
        ProviderId.Create("webhook").Value,
        ChannelType.Webhook,

        // Nothing. A 2xx means the endpoint accepted the request and that is the
        // end of what can be known.
        ProviderCapabilities.None,

        // Short, because there is no receipt coming and waiting achieves nothing.
        ExpectedReceiptWindow: TimeSpan.FromMinutes(1));

    private readonly WebhookOptions _options = options.Value;

    public ProviderDescriptor Descriptor => Self;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(
            new WebhookPayload(
                request.MessageId.Value,
                request.Body.Content,
                DateTimeOffset.UtcNow),
            Json);

        // The absolute URL from the recipient, not a path on a configured base
        // address. Recipient.Create has already required it to be https, which is
        // the one thing worth enforcing before making an outbound call to an
        // address a caller supplied.
        using var message = new HttpRequestMessage(HttpMethod.Post, request.Recipient.Address)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        Sign(message, payload, request);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return DeliveryResult.Timeout(
                $"The endpoint did not respond within {_options.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            // Includes DNS failures and refused connections, which are far more
            // common here than with a vendor: the address belongs to whoever
            // submitted the message and may simply not exist.
            return DeliveryResult.TransientFailure(
                $"Could not reach the endpoint: {exception.Message}");
        }

        using (response)
        {
            return Interpret(response);
        }
    }

    /// <summary>
    /// Signs the request so the receiver can tell it came from Relay.
    /// </summary>
    /// <remarks>
    /// HMAC-SHA256 over <c>timestamp.payload</c>, not over the payload alone. The
    /// timestamp is what makes the signature useless to replay: without it, anyone
    /// who observed one valid request could resend it forever and every copy would
    /// verify. Receivers are expected to reject a timestamp outside a few minutes.
    /// <para>
    /// The message id travels in its own header as well as in the body, so a
    /// receiver can deduplicate without parsing anything — Relay's own at-least-once
    /// delivery means the same webhook can legitimately arrive twice.
    /// </para>
    /// </remarks>
    private void Sign(HttpRequestMessage message, string payload, DeliveryRequest request)
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        byte[] signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_options.SigningSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));

        message.Headers.Add("X-Relay-Timestamp", timestamp);
        message.Headers.Add("X-Relay-Signature", $"sha256={Convert.ToHexStringLower(signature)}");
        message.Headers.Add("X-Relay-Message-Id", request.MessageId.Value.ToString());
        message.Headers.Add("X-Relay-Attempt", request.Attempt.ToString(CultureInfo.InvariantCulture));
    }

    private static DeliveryResult Interpret(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            // Accepted with no id, which the descriptor does not promise. The
            // difference from the other providers is worth being explicit about:
            // this message is now unreconcilable by design, not by omission.
            return DeliveryResult.Accepted();
        }

        string reason = $"The endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}.";

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => DeliveryResult.RateLimited(reason, RetryAfter(response)),

            // 4xx from a receiver means it understood and refused. Retrying sends
            // the same body to the same endpoint for the same answer — and unlike a
            // vendor API, there is no support desk to notice we are doing it.
            HttpStatusCode.BadRequest
                or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound
                or HttpStatusCode.Gone
                or HttpStatusCode.UnprocessableEntity => DeliveryResult.Rejected(reason),

            _ => DeliveryResult.TransientFailure(reason),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta;

    private static bool IsTimeout(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return exception is TaskCanceledException;
    }

    private sealed record WebhookPayload(
        [property: JsonPropertyName("message_id")] Guid MessageId,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("sent_at")] DateTimeOffset SentAt);
}

/// <summary>Settings for the webhook provider, bound from <c>Providers:webhook</c>.</summary>
public sealed class WebhookOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Providers:webhook";

    /// <summary>
    /// The shared secret used to sign outbound requests.
    /// </summary>
    /// <remarks>
    /// Receivers verify with the same value, so rotating it breaks every
    /// integration until they are updated. Supplied through the environment; the
    /// checked-in configuration ships the key with no value.
    /// </remarks>
    [Required]
    [MinLength(32)]
    public string SigningSecret { get; init; } = string.Empty;

    /// <summary>
    /// How long to wait for the endpoint to respond.
    /// </summary>
    /// <remarks>
    /// Shorter than the vendor providers. These endpoints belong to whoever
    /// submitted the message and are frequently slow; a generous timeout would let
    /// one badly written receiver hold worker slots that everyone else needs.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:00:30")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}
