using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Push.Beacon;

/// <summary>
/// Delivers push notifications through Beacon.
/// </summary>
/// <remarks>
/// The least capable provider in the system, and included for that reason. Beacon
/// accepts a message, returns an id, and then says nothing ever again: no
/// callbacks, no status endpoint.
/// <para>
/// So every message sent through it ends up abandoned by the sweeper rather than
/// resolved — Relay knows it was accepted and will never know whether it arrived.
/// That is not a defect to be worked around; it is the truth about push delivery,
/// where a device may be offline for days and the platform gives no feedback.
/// Declaring it is what stops the rest of the system pretending otherwise.
/// </para>
/// <para>
/// A design that assumed every provider reports delivery would need a special case
/// for this one. Capability flags mean it needs none: the sweeper reads the
/// descriptor, sees there is nobody to ask, and abandons with a reason that says
/// exactly that.
/// </para>
/// </remarks>
internal sealed class BeaconProvider(HttpClient client, IOptions<BeaconOptions> options)
    : IMessageProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ProviderDescriptor Self = new(
        ProviderId.Create("push.beacon").Value,
        ChannelType.Push,

        // ReturnsMessageId only. No pushed receipts, nothing queryable, no native
        // idempotency, and no Retry-After on a 429.
        ProviderCapabilities.ReturnsMessageId,

        // Short, because nothing will ever close the window. Waiting longer would
        // only delay the inevitable abandonment and hold rows in the sweeper's
        // index while it happened.
        ExpectedReceiptWindow: TimeSpan.FromMinutes(5));

    private readonly BeaconOptions _options = options.Value;

    public ProviderDescriptor Descriptor => Self;

    public async Task<DeliveryResult> SendAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new BeaconPush(
            request.Recipient.Address,
            request.Body.Subject ?? _options.DefaultTitle,
            request.Body.Content,

            // Beacon collapses notifications sharing a key, showing only the
            // newest. Keyed on the message id so Relay's own retries never
            // collapse two genuinely different notifications into one.
            request.MessageId.Value.ToString());

        HttpResponseMessage response;
        try
        {
            response = await client
                .PostAsJsonAsync("/v2/push", payload, Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsTimeout(exception))
        {
            return DeliveryResult.Timeout(
                $"Beacon did not respond within {_options.Timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException exception)
        {
            return DeliveryResult.TransientFailure($"Could not reach Beacon: {exception.Message}");
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
        if (response.IsSuccessStatusCode)
        {
            BeaconResponse? accepted = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(accepted?.Id)
                ? DeliveryResult.TransientFailure("Beacon accepted the push but returned no id.")
                : DeliveryResult.Accepted(accepted.Id);
        }

        BeaconResponse? failure = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        string reason = Mask(failure?.Error ?? response.ReasonPhrase ?? "no detail");

        return response.StatusCode switch
        {
            // No Retry-After: the descriptor does not claim ReportsRetryAfter, so
            // the caller falls back to its own configured backoff.
            HttpStatusCode.TooManyRequests => DeliveryResult.RateLimited(reason),

            // A device token Beacon no longer recognises. Permanent — the token
            // does not come back, and retrying spends the message budget on an
            // address that has ceased to exist.
            HttpStatusCode.Gone or HttpStatusCode.NotFound =>
                DeliveryResult.Rejected($"The device token is no longer registered. {reason}"),

            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                DeliveryResult.Rejected(reason),

            _ => DeliveryResult.TransientFailure(reason),
        };
    }

    private static async Task<BeaconResponse?> ReadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<BeaconResponse>(Json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string Mask(string detail) =>
        string.IsNullOrEmpty(_options.ApiKey)
            ? detail
            : detail.Replace(_options.ApiKey, "***", StringComparison.Ordinal);

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

    private sealed record BeaconPush(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("collapse_key")] string CollapseKey);

    private sealed record BeaconResponse(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("error")] string? Error);
}

/// <summary>Settings for the Beacon push provider, bound from <c>Providers:push.beacon</c>.</summary>
public sealed class BeaconOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Providers:push.beacon";

    /// <summary>Base address of the Beacon API.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>API key, sent as a bearer token.</summary>
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Title shown when a message carries no subject.
    /// </summary>
    /// <remarks>
    /// A push notification must have a title, and the Push channel treats a
    /// subject as optional. Rather than reject a valid message at the last
    /// moment, the provider supplies a configured default.
    /// </remarks>
    [Required]
    public string DefaultTitle { get; init; } = "Notification";

    /// <summary>How long to wait for a response before treating the silence as a timeout.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:02:00")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
