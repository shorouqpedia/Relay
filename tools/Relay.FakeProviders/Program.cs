// Stand-in upstreams, so the whole system runs with no external dependency and
// no credentials.
//
// These are not mocks that always succeed. Each one can be told to misbehave, and
// the default behaviour includes the failures that matter — because a compose
// stack where every provider always works demonstrates nothing about a system
// built to survive providers that do not.

using System.Globalization;
using System.Text.Json;
using Relay.FakeProviders;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<UpstreamBehaviourStore>();
builder.Services.AddSingleton<PendingReceipts>();
builder.Services.AddHostedService<ReceiptSender>();
builder.Services.AddHttpClient();

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "Relay fake providers",
    upstreams = Upstreams.Names,
    control = "POST /_control/{upstream}/behaviour with { \"behaviour\": \"accept|reject|rate_limit|server_error|hang|silent\" }",
}));

// ── Control plane ────────────────────────────────────────────────────────────
//
// Lets a demo or a test drive the upstreams into the states that are otherwise
// impossible to produce on purpose: a provider that rate-limits, one that hangs,
// one that accepts messages and then never sends a receipt.

app.MapPost("/_control/{upstream}/behaviour", (
    string upstream,
    BehaviourRequest request,
    UpstreamBehaviourStore store) =>
{
    // Underscores stripped before parsing, so `server_error` and `ServerError`
    // both work. The documented spelling is snake_case because that is what the
    // rest of this tool's API uses, and an enum name would not match it.
    string normalised = (request.Behaviour ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal);

    if (!Enum.TryParse(normalised, ignoreCase: true, out FakeBehaviour behaviour))
    {
        return Results.BadRequest(new
        {
            error = $"Unknown behaviour '{request.Behaviour}'.",
            accepted = Enum.GetNames<FakeBehaviour>(),
        });
    }

    store.Set(upstream, behaviour);

    return Results.Ok(new { upstream, behaviour = behaviour.ToString() });
});

app.MapGet("/_control", (UpstreamBehaviourStore store) => Results.Ok(store.All()));

// ── Postal: JSON, hex signature over "{timestamp}.{body}" ────────────────────

app.MapPost("/postal/v1/messages", async (
    HttpContext context,
    UpstreamBehaviourStore store,
    PendingReceipts receipts,
    IConfiguration configuration) =>
{
    IResult? misbehaviour = await Upstreams
        .ApplyAsync(store, "postal", context.RequestAborted)
        .ConfigureAwait(false);

    if (misbehaviour is not null)
    {
        return misbehaviour;
    }

    string id = $"postal-{Guid.CreateVersion7():N}";

    // Queued rather than sent inline. A provider that reports delivery before it
    // has answered the send would be a provider Relay never has to reconcile,
    // which is the one case the system does not need to handle.
    receipts.Enqueue(new PendingReceipt(
        Upstream: "postal",
        CallbackUrl: configuration["Relay:CallbackBaseUrl"] + "/api/v1/callbacks/email.postal",
        ProviderMessageId: id,
        DueAt: DateTimeOffset.UtcNow.AddSeconds(3),
        Silent: store.Get("postal") is FakeBehaviour.Silent));

    return Results.Accepted(value: new { id });
});

app.MapGet("/postal/v1/messages/{id}", (string id, UpstreamBehaviourStore store) =>
    store.Get("postal") is FakeBehaviour.Silent
        ? Results.Ok(new { status = "queued" })
        : Results.Ok(new { status = "delivered" }));

// ── Mailhook: JSON, base64 signature over "{body}{timestamp}", batched ───────

app.MapPost("/mailhook/messages/send", async (
    HttpContext context,
    UpstreamBehaviourStore store,
    PendingReceipts receipts,
    IConfiguration configuration) =>
{
    IResult? misbehaviour = await Upstreams
        .ApplyAsync(store, "mailhook", context.RequestAborted)
        .ConfigureAwait(false);

    if (misbehaviour is not null)
    {
        return misbehaviour;
    }

    string id = $"mh-{Guid.CreateVersion7():N}";

    receipts.Enqueue(new PendingReceipt(
        "mailhook",
        configuration["Relay:CallbackBaseUrl"] + "/api/v1/callbacks/email.mailhook",
        id,
        DateTimeOffset.UtcNow.AddSeconds(4),
        store.Get("mailhook") is FakeBehaviour.Silent));

    return Results.Accepted(value: new { message_id = id });
});

app.MapGet("/mailhook/messages/{id}", (string id) => Results.Ok(new { state = "delivered" }));

// ── Twinkle: form-encoded, and HTTP 200 for everything ──────────────────────

app.MapPost("/twinkle/sms/send", async (
    HttpContext context,
    UpstreamBehaviourStore store,
    PendingReceipts receipts,
    IConfiguration configuration) =>
{
    // Note the status codes below. Twinkle answers 200 even when refusing, which
    // is the property the real provider has and the reason it is worth faking.
    FakeBehaviour behaviour = store.Get("twinkle");

    if (behaviour is FakeBehaviour.Hang)
    {
        await Task.Delay(Timeout.Infinite, context.RequestAborted).ConfigureAwait(false);
    }

    if (behaviour is FakeBehaviour.RateLimit)
    {
        return Results.Ok(new { status = "rate_limited", detail = "Throughput cap reached.", retry_after = 20 });
    }

    if (behaviour is FakeBehaviour.Reject)
    {
        return Results.Ok(new { status = "blocked", detail = "This number has opted out." });
    }

    if (behaviour is FakeBehaviour.ServerError)
    {
        return Results.Ok(new { status = "carrier_unavailable", detail = "Carrier is not responding." });
    }

    string id = $"tw-{Guid.CreateVersion7():N}";

    receipts.Enqueue(new PendingReceipt(
        "twinkle",
        configuration["Relay:CallbackBaseUrl"] + "/api/v1/callbacks/sms.twinkle",
        id,
        DateTimeOffset.UtcNow.AddSeconds(2),
        behaviour is FakeBehaviour.Silent));

    return Results.Ok(new { status = "queued", message_id = id });
});

// ── Beacon: accepts, and then says nothing, ever ────────────────────────────

app.MapPost("/beacon/v2/push", async (HttpContext context, UpstreamBehaviourStore store) =>
{
    IResult? misbehaviour = await Upstreams
        .ApplyAsync(store, "beacon", context.RequestAborted)
        .ConfigureAwait(false);

    // No receipt is ever queued for Beacon, and that is faithful rather than
    // lazy: the real provider has no callbacks and no status endpoint, so every
    // message through it is abandoned by the sweeper. Running the stack shows
    // that happening.
    return misbehaviour ?? Results.Ok(new { id = $"bc-{Guid.CreateVersion7():N}" });
});

// ── An inbound webhook receiver, so the webhook provider has somewhere to go ──

app.MapPost("/receiver/inbound", (HttpContext context, ILogger<Program> logger) =>
{
    logger.LogInformation(
        "Webhook received. Signature {Signature}, message {MessageId}.",
        context.Request.Headers["X-Relay-Signature"].ToString(),
        context.Request.Headers["X-Relay-Message-Id"].ToString());

    return Results.NoContent();
});

await app.RunAsync().ConfigureAwait(false);

/// <summary>The behaviour a control request asks for.</summary>
/// <param name="Behaviour">One of the <see cref="FakeBehaviour"/> names.</param>
internal sealed record BehaviourRequest(string Behaviour);

/// <summary>Shared misbehaviour, applied the same way for the JSON upstreams.</summary>
internal static class Upstreams
{
    /// <summary>The upstreams this tool fakes.</summary>
    public static readonly string[] Names = ["postal", "mailhook", "twinkle", "beacon"];

    /// <summary>
    /// Returns a failure response when the upstream has been told to misbehave.
    /// </summary>
    /// <returns><see langword="null"/> when the request should be handled normally.</returns>
    public static async Task<IResult?> ApplyAsync(
        UpstreamBehaviourStore store,
        string upstream,
        CancellationToken cancellationToken)
    {
        switch (store.Get(upstream))
        {
            case FakeBehaviour.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return null;

            case FakeBehaviour.RateLimit:
                return Results.Json(
                    new { error = "rate_limited", message = "Quota reached." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            case FakeBehaviour.Reject:
                return Results.Json(
                    new { error = "rejected", message = "This recipient is suppressed." },
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            case FakeBehaviour.ServerError:
                return Results.Json(
                    new { error = "unavailable", message = "Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);

            default:
                return null;
        }
    }

    /// <summary>Formats a Unix-second timestamp the way the upstreams sign with.</summary>
    public static string UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
