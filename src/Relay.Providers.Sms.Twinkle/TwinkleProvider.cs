using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Sms.Twinkle;

/// <summary>
/// Delivers SMS through Twinkle.
/// </summary>
/// <remarks>
/// Twinkle answers <b>HTTP 200 for everything</b>, including failures, and puts
/// the real outcome in a <c>status</c> field. This is common enough in older SMS
/// APIs to be worth having in the codebase, because it breaks the assumption
/// almost every HTTP client is built on: that the status line tells you whether
/// it worked.
/// <para>
/// A resilience library configured on HTTP status codes sees nothing but success
/// here and never retries. A provider that checks <c>IsSuccessStatusCode</c>
/// reports every rejection as delivered. The classification has to read the body,
/// which is precisely why classification is the provider's job and not the
/// pipeline's (ADR 0003).
/// </para>
/// <para>
/// Twinkle also charges per segment and rejects anything over its limit — but it
/// does so with the same 200, so that too arrives as a status code in a payload.
/// </para>
/// </remarks>
internal sealed class TwinkleProvider(HttpClient client, IOptions<TwinkleOptions> options)
    : IMessageProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ProviderDescriptor Self = new(
        ProviderId.Create("sms.twinkle").Value,
        ChannelType.Sms,

        // No QueryableReceipts: Twinkle has no status endpoint. When a receipt
        // does not arrive, there is nobody to ask, and the sweeper can only
        // abandon the message. That is a real operational cost of this provider,
        // and declaring it is what lets the router and the sweeper account for it
        // instead of discovering it per message.
        ProviderCapabilities.ReturnsMessageId
        | ProviderCapabilities.PushesDeliveryReceipts
        | ProviderCapabilities.ReportsRetryAfter,

        // Minutes, not hours. An SMS receipt comes back through the carrier
        // quickly or not at all, so waiting an email-shaped window would leave
        // messages unresolved long after their fate was decided.
        ExpectedReceiptWindow: TimeSpan.FromMinutes(15));

    private readonly TwinkleOptions _options = options.Value;

    public ProviderDescriptor Descriptor => Self;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        // Form-encoded, not JSON. Twinkle predates the convention, and the
        // difference stops at this assembly's boundary.
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["account"] = _options.AccountId,
            ["from"] = _options.SenderId,
            ["to"] = request.Recipient.Address,
            ["text"] = request.Body.Content,
        });

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("/sms/send", content, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return DeliveryResult.Timeout(
                $"Twinkle did not respond within {_options.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            return DeliveryResult.TransientFailure($"Could not reach Twinkle: {exception.Message}");
        }

        using (response)
        {
            return await InterpretAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DeliveryResult> InterpretAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // The status line is checked, but only to catch the cases Twinkle cannot
        // dress up as success — a gateway between us and them returning 502, say.
        // Anything Twinkle itself produced arrives as 200.
        if (!response.IsSuccessStatusCode)
        {
            return DeliveryResult.TransientFailure(
                $"Twinkle returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        TwinkleResponse? body;
        try
        {
            body = await response.Content
                .ReadFromJsonAsync<TwinkleResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return DeliveryResult.TransientFailure("Twinkle returned a body that could not be parsed.");
        }

        if (body is null)
        {
            return DeliveryResult.TransientFailure("Twinkle returned an empty body.");
        }

        // The whole reason this provider exists in the codebase. Every branch below
        // is reached with an HTTP 200.
        return body.Status?.ToLowerInvariant() switch
        {
            "queued" or "sent" or "accepted" => Accept(body),

            // Terminal on Twinkle's side. Retrying re-asks a settled question and
            // is charged for each time.
            "invalid_number" or "blocked" or "opted_out" or "message_too_long" =>
                DeliveryResult.Rejected(Describe(body)),

            "rate_limited" => DeliveryResult.RateLimited(Describe(body), RetryAfter(body)),

            "internal_error" or "carrier_unavailable" =>
                DeliveryResult.TransientFailure(Describe(body)),

            // An outcome Twinkle has added since this was written. Transient rather
            // than rejected, so an unrecognised code costs a retry instead of
            // discarding a message that might have been deliverable — and the
            // reason carries the code so it shows up in the logs.
            _ => DeliveryResult.TransientFailure(
                $"Twinkle returned an unrecognised status '{body.Status}'. {Describe(body)}"),
        };
    }

    private static DeliveryResult Accept(TwinkleResponse body) =>
        string.IsNullOrWhiteSpace(body.MessageId)
            ? DeliveryResult.TransientFailure("Twinkle accepted the message but returned no id.")
            : DeliveryResult.Accepted(body.MessageId);

    private static TimeSpan? RetryAfter(TwinkleResponse body) =>
        body.RetryAfterSeconds is > 0 ? TimeSpan.FromSeconds(body.RetryAfterSeconds.Value) : null;

    private string Describe(TwinkleResponse body)
    {
        string detail = body.Detail ?? body.Status ?? "no detail";

        return string.IsNullOrEmpty(_options.ApiKey)
            ? detail
            : detail.Replace(_options.ApiKey, "***", StringComparison.Ordinal);
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

    private sealed record TwinkleResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("retry_after")] int? RetryAfterSeconds);
}

/// <summary>Settings for the Twinkle SMS provider, bound from <c>Providers:sms.twinkle</c>.</summary>
public sealed class TwinkleOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Providers:sms.twinkle";

    /// <summary>Base address of the Twinkle API.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Twinkle account identifier, sent with every request.</summary>
    [Required]
    public string AccountId { get; init; } = string.Empty;

    /// <summary>API key, sent as a bearer token.</summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// The sender the recipient sees.
    /// </summary>
    /// <remarks>
    /// A short alphanumeric string or a phone number, depending on what the
    /// destination country permits. Not validated here beyond being present —
    /// which country a number belongs to is Twinkle's business, and guessing at it
    /// would reject valid configurations.
    /// </remarks>
    [Required]
    public string SenderId { get; init; } = string.Empty;

    /// <summary>How long to wait for a response before treating the silence as a timeout.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
