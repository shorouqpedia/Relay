using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Email.Postal;

/// <summary>
/// Verifies and reads Postal's delivery callbacks.
/// </summary>
/// <remarks>
/// Postal signs <c>{timestamp}.{body}</c> with HMAC-SHA256 and sends the result
/// hex-encoded in <c>X-Postal-Signature</c>, prefixed with the algorithm. One
/// event per callback.
/// </remarks>
internal sealed class PostalCallbackReceiver(IOptions<PostalOptions> options) : ICallbackReceiver
{
    private const string SignatureHeader = "X-Postal-Signature";
    private const string TimestampHeader = "X-Postal-Timestamp";
    private const string SignaturePrefix = "sha256=";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly PostalOptions _options = options.Value;

    public CallbackOutcome Receive(InboundCallback callback)
    {
        string? timestamp = callback.Header(TimestampHeader);
        string? signature = callback.Header(SignatureHeader);

        if (timestamp is null || signature is null)
        {
            return CallbackOutcome.Rejected("The signature or timestamp header was missing.");
        }

        // Checked before the signature, because an expired-but-valid signature and
        // a forged one are the same verdict, and the timestamp check is the cheap
        // one. Order is a performance detail here, not a security one.
        if (!CallbackSignature.IsWithinTolerance(
                timestamp, callback.ReceivedAt, CallbackSignature.DefaultTolerance))
        {
            return CallbackOutcome.Rejected(
                $"The timestamp {timestamp} is outside the accepted window.");
        }

        string expected = SignaturePrefix + CallbackSignature.ComputeHex(
            _options.CallbackSecret,
            $"{timestamp}.{callback.Body}");

        if (!CallbackSignature.Matches(expected, signature))
        {
            return CallbackOutcome.Rejected("The signature did not verify.");
        }

        return Parse(callback.Body);
    }

    private static CallbackOutcome Parse(string body)
    {
        PostalCallback? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PostalCallback>(body, Json);
        }
        catch (JsonException exception)
        {
            // Correctly signed and still unreadable, which means Postal changed
            // its payload. Distinguished from a rejection because the two need
            // different responses: this one is a bug to fix, not an intrusion to
            // shrug off, and returning 401 for it would hide that.
            return CallbackOutcome.Malformed($"The callback body did not parse: {exception.Message}");
        }

        if (parsed?.MessageId is null || parsed.Event is null)
        {
            return CallbackOutcome.Malformed("The callback did not name a message and an event.");
        }

        ReceiptStatus status = parsed.Event.ToLowerInvariant() switch
        {
            "message.delivered" => ReceiptStatus.Delivered,
            "message.bounced" or "message.failed" => ReceiptStatus.Failed,

            // Postal sends opens and clicks to the same endpoint. They say nothing
            // about delivery and must not be treated as an outcome — but they are
            // not malformed either, so the callback is accepted carrying nothing.
            _ => ReceiptStatus.Pending,
        };

        if (status is ReceiptStatus.Pending)
        {
            return CallbackOutcome.Accepted([]);
        }

        return CallbackOutcome.Accepted([
            new ProviderReceipt(
                parsed.MessageId,
                status,
                parsed.Detail,
                parsed.OccurredAt),
        ]);
    }

    private sealed record PostalCallback(
        [property: JsonPropertyName("event")] string? Event,
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("detail")] string? Detail,
        [property: JsonPropertyName("occurred_at")] DateTimeOffset? OccurredAt);
}
