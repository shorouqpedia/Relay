using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Email.Mailhook;

/// <summary>
/// Delivers email through Mailhook.
/// </summary>
/// <remarks>
/// Written against <see cref="HttpClient"/> directly rather than through Refit,
/// unlike the Postal provider. Both styles are in the codebase deliberately: the
/// declarative client is better when an API is regular, and a hand-written one is
/// better when the interesting behaviour is in interpreting the response — here,
/// a rate limit expressed as an epoch timestamp in a vendor header.
/// <para>
/// The point of the plugin boundary is that this choice is invisible from
/// outside. Nothing above <see cref="IMessageProvider"/> knows or could find out
/// which of the two this provider uses.
/// </para>
/// </remarks>
internal sealed class MailhookProvider(HttpClient client, IOptions<MailhookOptions> options)
    : IMessageProvider, IReceiptQueryable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ProviderDescriptor Self = new(
        ProviderId.Create("email.mailhook").Value,
        ChannelType.Email,

        // No NativeIdempotency: Mailhook has no equivalent, so a retry that
        // reaches it does produce a second email. Relay's own guarantee is
        // unaffected — it never depended on the provider's (ADR 0008) — which is
        // exactly why a provider is allowed to lack this.
        ProviderCapabilities.ReturnsMessageId
        | ProviderCapabilities.PushesDeliveryReceipts
        | ProviderCapabilities.QueryableReceipts
        | ProviderCapabilities.ReportsRetryAfter,
        ExpectedReceiptWindow: TimeSpan.FromHours(4));

    private readonly MailhookOptions _options = options.Value;

    public ProviderDescriptor Descriptor => Self;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new MailhookSend(
            _options.FromAddress,
            request.Recipient.Address,
            request.Body.Subject,
            request.Body.Content);

        HttpResponseMessage response;
        try
        {
            response = await client
                .PostAsJsonAsync("/messages/send", payload, Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return DeliveryResult.Timeout(
                $"Mailhook did not respond within {_options.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            return DeliveryResult.TransientFailure($"Could not reach Mailhook: {exception.Message}");
        }

        using (response)
        {
            return await InterpretAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ReceiptStatus> QueryReceiptAsync(
        string providerMessageId,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client
                .GetAsync($"/messages/{Uri.EscapeDataString(providerMessageId)}", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            return ReceiptStatus.Unknown;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return ReceiptStatus.Unknown;
            }

            MailhookStatus? status = await ReadAsync<MailhookStatus>(response, cancellationToken)
                .ConfigureAwait(false);

            return status?.State?.ToLowerInvariant() switch
            {
                "delivered" => ReceiptStatus.Delivered,
                "bounced" or "dropped" or "rejected" => ReceiptStatus.Failed,
                "queued" or "accepted" => ReceiptStatus.Pending,
                _ => ReceiptStatus.Unknown,
            };
        }
    }

    private async Task<DeliveryResult> InterpretAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            MailhookSendResponse? accepted =
                await ReadAsync<MailhookSendResponse>(response, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(accepted?.MessageId)
                ? DeliveryResult.TransientFailure(
                    "Mailhook accepted the message but returned no message id.")
                : DeliveryResult.Accepted(accepted.MessageId);
        }

        string reason = await DescribeFailureAsync(response, cancellationToken).ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests =>
                DeliveryResult.RateLimited(reason, RetryAfter(response)),

            HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound => DeliveryResult.Rejected(reason),

            HttpStatusCode.Unauthorized => DeliveryResult.TransientFailure(reason),
            _ => DeliveryResult.TransientFailure(reason),
        };
    }

    /// <summary>
    /// Reads Mailhook's rate-limit signal.
    /// </summary>
    /// <remarks>
    /// Mailhook does not send <c>Retry-After</c>. It sends
    /// <c>X-RateLimit-Reset</c> as a Unix timestamp, which has to be turned into a
    /// duration relative to now — and can be in the past by the time it arrives,
    /// in which case there is nothing to wait for.
    /// <para>
    /// This is the kind of per-provider peculiarity the plugin boundary exists to
    /// absorb. The router sees a <see cref="TimeSpan"/> and never learns that one
    /// provider counts forwards and another counts backwards.
    /// </para>
    /// </remarks>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Reset", out IEnumerable<string>? values))
        {
            return null;
        }

        string? raw = values.FirstOrDefault();

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            return null;
        }

        TimeSpan delay = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;

        return delay > TimeSpan.Zero ? delay : null;
    }

    private async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        string detail = TryReadError(body) ?? response.ReasonPhrase ?? "no detail";

        // Masked before it leaves the provider. Mailhook echoes request headers
        // into its error payloads, and a failure reason is logged.
        return string.IsNullOrEmpty(_options.ApiKey)
            ? Truncate(detail)
            : Truncate(detail.Replace(_options.ApiKey, "***", StringComparison.Ordinal));
    }

    private static string? TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            MailhookError? parsed = JsonSerializer.Deserialize<MailhookError>(body, Json);
            return parsed?.Message ?? parsed?.Error ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static async Task<T?> ReadAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<T>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A success status carrying a body that is not what it claims. Reported
            // as "no content" and handled by the caller as a transient failure,
            // rather than escaping as an exception the contract forbids.
            return default;
        }
    }

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

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : string.Concat(value.AsSpan(0, 500), "…");

    private sealed record MailhookSend(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("html")] string Html);

    private sealed record MailhookSendResponse(
        [property: JsonPropertyName("message_id")] string? MessageId);

    private sealed record MailhookStatus(
        [property: JsonPropertyName("state")] string? State);

    private sealed record MailhookError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("message")] string? Message);
}
