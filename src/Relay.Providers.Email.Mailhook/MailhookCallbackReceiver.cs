using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Email.Mailhook;

/// <summary>
/// Verifies and reads Mailhook's delivery callbacks.
/// </summary>
/// <remarks>
/// Mailhook differs from Postal in three ways that all stop here: it signs the
/// body alone with the timestamp sent as a separate signed field rather than
/// prefixed, encodes the result base64 rather than hex, and <b>batches</b> —
/// one callback can report fifty messages.
/// <para>
/// Batching is why <see cref="CallbackOutcome"/> carries a list. Modelling a
/// callback as one receipt would have forced this provider to either lie or be
/// special-cased upstream, and the second is what the plugin boundary exists to
/// avoid.
/// </para>
/// </remarks>
internal sealed class MailhookCallbackReceiver(IOptions<MailhookOptions> options) : ICallbackReceiver
{
    private const string SignatureHeader = "X-Mailhook-Signature";
    private const string TimestampHeader = "X-Mailhook-Timestamp";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MailhookOptions _options = options.Value;

    public CallbackOutcome Receive(InboundCallback callback)
    {
        string? timestamp = callback.Header(TimestampHeader);
        string? signature = callback.Header(SignatureHeader);

        if (timestamp is null || signature is null)
        {
            return CallbackOutcome.Rejected("The signature or timestamp header was missing.");
        }

        if (!CallbackSignature.IsWithinTolerance(
                timestamp, callback.ReceivedAt, CallbackSignature.DefaultTolerance))
        {
            return CallbackOutcome.Rejected(
                $"The timestamp {timestamp} is outside the accepted window.");
        }

        // Mailhook concatenates the other way round: body first, then timestamp.
        // A detail with no significance beyond being what Mailhook does — and
        // precisely the kind of thing that makes a single shared verifier
        // impossible without a switch on provider id.
        string expected = CallbackSignature.ComputeBase64(
            _options.CallbackSecret,
            $"{callback.Body}{timestamp}");

        if (!CallbackSignature.Matches(expected, signature))
        {
            return CallbackOutcome.Rejected("The signature did not verify.");
        }

        return Parse(callback.Body);
    }

    private static CallbackOutcome Parse(string body)
    {
        MailhookBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<MailhookBatch>(body, Json);
        }
        catch (JsonException exception)
        {
            return CallbackOutcome.Malformed($"The callback body did not parse: {exception.Message}");
        }

        if (batch?.Events is null)
        {
            return CallbackOutcome.Malformed("The callback carried no events array.");
        }

        List<ProviderReceipt> receipts = [];

        foreach (MailhookEvent item in batch.Events)
        {
            if (item.MessageId is null)
            {
                // One unusable entry in a batch does not invalidate the rest. The
                // alternative — rejecting the whole callback — would make Mailhook
                // resend forty-nine receipts that were fine, forever, because the
                // fiftieth will never improve.
                continue;
            }

            ReceiptStatus status = item.Type?.ToLowerInvariant() switch
            {
                "delivered" => ReceiptStatus.Delivered,
                "bounce" or "dropped" or "complaint" => ReceiptStatus.Failed,
                _ => ReceiptStatus.Pending,
            };

            if (status is not ReceiptStatus.Pending)
            {
                receipts.Add(new ProviderReceipt(item.MessageId, status, item.Reason, item.At));
            }
        }

        return CallbackOutcome.Accepted(receipts);
    }

    private sealed record MailhookBatch(
        [property: JsonPropertyName("events")] IReadOnlyList<MailhookEvent>? Events);

    private sealed record MailhookEvent(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("message_id")] string? MessageId,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("at")] DateTimeOffset? At);
}
