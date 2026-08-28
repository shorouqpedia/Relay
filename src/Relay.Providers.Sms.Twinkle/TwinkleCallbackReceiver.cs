using System.Web;
using Microsoft.Extensions.Options;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Sms.Twinkle;

/// <summary>
/// Verifies and reads Twinkle's delivery callbacks.
/// </summary>
/// <remarks>
/// Twinkle posts <c>application/x-www-form-urlencoded</c> rather than JSON, and
/// signs the raw body without a timestamp of its own — the timestamp is a field
/// <i>inside</i> the form, which is included in what gets signed and so cannot be
/// altered without breaking the signature.
/// <para>
/// That is a weaker construction than a header, because the replay window depends
/// on a field the parser has to read before the timestamp check can happen. It is
/// still checked, and the order below is deliberate: verify first, then parse,
/// then check the age. Reading a field out of an unverified body to decide
/// whether to verify it would be exactly backwards.
/// </para>
/// </remarks>
internal sealed class TwinkleCallbackReceiver(IOptions<TwinkleOptions> options) : ICallbackReceiver
{
    private const string SignatureHeader = "X-Twinkle-Signature";

    private readonly TwinkleOptions _options = options.Value;

    public CallbackOutcome Receive(InboundCallback callback)
    {
        string? signature = callback.Header(SignatureHeader);

        if (signature is null)
        {
            return CallbackOutcome.Rejected("The signature header was missing.");
        }

        string expected = CallbackSignature.ComputeHex(_options.CallbackSecret, callback.Body);

        if (!CallbackSignature.Matches(expected, signature))
        {
            return CallbackOutcome.Rejected("The signature did not verify.");
        }

        // Only now is the body trustworthy enough to read.
        System.Collections.Specialized.NameValueCollection form =
            HttpUtility.ParseQueryString(callback.Body);

        string? timestamp = form["timestamp"];

        if (!CallbackSignature.IsWithinTolerance(
                timestamp, callback.ReceivedAt, CallbackSignature.DefaultTolerance))
        {
            return CallbackOutcome.Rejected(
                $"The timestamp {timestamp} is outside the accepted window.");
        }

        string? messageId = form["message_id"];
        string? status = form["status"];

        if (messageId is null || status is null)
        {
            return CallbackOutcome.Malformed("The callback did not name a message and a status.");
        }

        ReceiptStatus receipt = status.ToLowerInvariant() switch
        {
            "delivered" => ReceiptStatus.Delivered,

            // Twinkle reports both "the handset rejected it" and "the carrier gave
            // up" as undelivered. They are different operationally and identical
            // to Relay: the message did not arrive and will not.
            "undelivered" or "failed" or "expired" => ReceiptStatus.Failed,
            _ => ReceiptStatus.Pending,
        };

        return receipt is ReceiptStatus.Pending
            ? CallbackOutcome.Accepted([])
            : CallbackOutcome.Accepted([
                new ProviderReceipt(messageId, receipt, form["error"], null),
            ]);
    }
}
