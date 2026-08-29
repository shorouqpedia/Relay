using System.Globalization;
using System.Text;
using System.Text.Json;
using Relay.Providers.Abstractions;

namespace Relay.FakeProviders;

/// <summary>
/// Sends the delivery receipts the fake upstreams promised.
/// </summary>
/// <remarks>
/// Signs them the way each real provider does, which is the point: the callback
/// endpoint's verification is exercised for real, and a mistake in either the
/// signing or the verifying shows up as a 401 in the logs of a running stack
/// rather than only in a test.
/// <para>
/// Uses the same <see cref="CallbackSignature"/> helpers the verifiers use. That
/// is a deliberate weakness — a bug in the shared helper would be invisible here,
/// because both sides would make it identically. The unit tests cover the helper
/// itself for exactly that reason; this covers the wiring around it.
/// </para>
/// </remarks>
internal sealed class ReceiptSender(
    PendingReceipts queue,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<ReceiptSender> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (PendingReceipt receipt in queue.TakeDue(DateTimeOffset.UtcNow))
            {
                if (receipt.Silent)
                {
                    logger.LogInformation(
                        "Dropping the receipt for {ProviderMessageId}: {Upstream} is set to stay silent.",
                        receipt.ProviderMessageId,
                        receipt.Upstream);

                    continue;
                }

                await SendAsync(receipt, stoppingToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendAsync(PendingReceipt receipt, CancellationToken cancellationToken)
    {
        string secret = configuration[$"Upstreams:{receipt.Upstream}:CallbackSecret"]
            ?? "relay-local-development-callback-secret";

        using HttpRequestMessage request = Build(receipt, secret);

        try
        {
            using HttpResponseMessage response = await clients
                .CreateClient()
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation(
                "Reported {ProviderMessageId} to Relay: {StatusCode}.",
                receipt.ProviderMessageId,
                (int)response.StatusCode);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Relay may not be up yet, or may be restarting. A real provider would
            // retry; this one drops it, because a development tool that queues
            // receipts forever is a development tool that floods the logs after a
            // restart.
            logger.LogWarning(
                "Could not report {ProviderMessageId}: {Reason}",
                receipt.ProviderMessageId,
                exception.Message);
        }
    }

    private static HttpRequestMessage Build(PendingReceipt receipt, string secret)
    {
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);

        (string body, string contentType, string signatureHeader, string signature) =
            receipt.Upstream switch
            {
                "postal" => Postal(receipt, secret, timestamp),
                "mailhook" => Mailhook(receipt, secret, timestamp),
                "twinkle" => Twinkle(receipt, secret, timestamp),
                _ => throw new InvalidOperationException(
                    $"No receipt format is defined for upstream '{receipt.Upstream}'."),
            };

        var request = new HttpRequestMessage(HttpMethod.Post, receipt.CallbackUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };

        request.Headers.Add(signatureHeader, signature);

        if (receipt.Upstream is "postal")
        {
            request.Headers.Add("X-Postal-Timestamp", timestamp);
        }
        else if (receipt.Upstream is "mailhook")
        {
            request.Headers.Add("X-Mailhook-Timestamp", timestamp);
        }

        return request;
    }

    private static (string, string, string, string) Postal(
        PendingReceipt receipt,
        string secret,
        string timestamp)
    {
        string body = JsonSerializer.Serialize(new
        {
            @event = "message.delivered",
            message_id = receipt.ProviderMessageId,
            occurred_at = DateTimeOffset.UtcNow,
        });

        return (
            body,
            "application/json",
            "X-Postal-Signature",
            "sha256=" + CallbackSignature.ComputeHex(secret, $"{timestamp}.{body}"));
    }

    private static (string, string, string, string) Mailhook(
        PendingReceipt receipt,
        string secret,
        string timestamp)
    {
        // A batch of one. The shape is what matters — Relay has to read an array
        // here where Postal sends a single object.
        string body = JsonSerializer.Serialize(new
        {
            events = new[]
            {
                new
                {
                    type = "delivered",
                    message_id = receipt.ProviderMessageId,
                    at = DateTimeOffset.UtcNow,
                },
            },
        });

        return (
            body,
            "application/json",
            "X-Mailhook-Signature",
            CallbackSignature.ComputeBase64(secret, $"{body}{timestamp}"));
    }

    private static (string, string, string, string) Twinkle(
        PendingReceipt receipt,
        string secret,
        string timestamp)
    {
        // Form-encoded, with the timestamp inside the signed body rather than in a
        // header — which is how the real Twinkle does it, and why its verifier has
        // to verify before it can parse.
        string body =
            $"message_id={Uri.EscapeDataString(receipt.ProviderMessageId)}"
            + $"&status=delivered&timestamp={timestamp}";

        return (
            body,
            "application/x-www-form-urlencoded",
            "X-Twinkle-Signature",
            CallbackSignature.ComputeHex(secret, body));
    }
}
