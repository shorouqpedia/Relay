using System.Net;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using Relay.Providers.ContractTests.Fakes;
using Relay.Providers.Email.Mailhook;

namespace Relay.Providers.ContractTests.Providers;

/// <summary>
/// Runs the provider contract against Mailhook.
/// </summary>
/// <remarks>
/// Mailhook signals a rate limit with <c>X-RateLimit-Reset</c> as a Unix
/// timestamp instead of <c>Retry-After</c> as a duration, and authenticates with
/// a vendor header instead of a bearer token. None of that reaches the contract:
/// the same twelve scenarios run unchanged.
/// </remarks>
public sealed class MailhookContractTests : ProviderContract, IDisposable
{
    private const string ApiKey = IControllableUpstream.SecretMarker;
    private const string BlockedAddress = "bounced@example.com";

    private readonly FakeHttpUpstream _upstream;
    private readonly HttpClient _httpClient;
    private readonly MailhookProvider _provider;

    public MailhookContractTests()
    {
        _upstream = new FakeHttpUpstream(Respond);

        _httpClient = new HttpClient(_upstream)
        {
            BaseAddress = new Uri("https://mailhook.test"),
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        _provider = new MailhookProvider(
            _httpClient,
            Options.Create(new MailhookOptions
            {
                BaseUrl = "https://mailhook.test",
                ApiKey = ApiKey,
                FromAddress = "relay@example.com",
                Timeout = TimeSpan.FromMilliseconds(500),
            }));
    }

    protected override IMessageProvider Provider => _provider;

    protected override IControllableUpstream Upstream => _upstream;

    protected override DeliveryRequest ValidRequest() => Request("recipient@example.com");

    protected override DeliveryRequest PermanentlyRejectedRequest() => Request(BlockedAddress);

    [Fact]
    public async Task PC09a_Send_WhenTheBodyCannotBeParsed_ReportsATransientFailure()
    {
        Upstream.Behave(UpstreamBehaviour.MalformedResponse);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Sharper than PC09, and stated here rather than in the shared contract
        // because it is only true of providers that read a body to decide whether
        // they succeeded. Mailhook does; the webhook provider does not.
        //
        // Reporting success on an unreadable body would record a message as sent
        // on the strength of a response nobody could interpret.
        result.Outcome.ShouldBe(AttemptOutcome.TransientFailure);
    }

    private static DeliveryRequest Request(string address) => new(
        MessageId.New(),
        Recipient.Create(ChannelType.Email, address).Value,
        MessageBody.Create(ChannelType.Email, "Body text.", "Subject").Value,
        IdempotencyKey.Create("contract-test-key-02").Value,
        Attempt: 1);

    private static HttpResponseMessage Respond(UpstreamBehaviour behaviour, HttpRequestMessage request)
    {
        if (RequestTargets(request, BlockedAddress))
        {
            return FakeHttpUpstream.Json(
                HttpStatusCode.UnprocessableEntity,
                """{"error":"hard_bounce","message":"This address previously hard bounced."}""");
        }

        return behaviour switch
        {
            UpstreamBehaviour.Accept => FakeHttpUpstream.Json(
                HttpStatusCode.Accepted,
                """{"message_id":"mh-0001"}"""),

            UpstreamBehaviour.ServerError => FakeHttpUpstream.Json(
                HttpStatusCode.ServiceUnavailable,
                """{"error":"maintenance","message":"Back shortly."}"""),

            // The header, not Retry-After, and an absolute instant rather than a
            // duration. The provider converts it; the contract never sees it.
            UpstreamBehaviour.RateLimit => WithRateLimitReset(
                FakeHttpUpstream.Json(
                    HttpStatusCode.TooManyRequests,
                    """{"error":"rate_limited","message":"Per-minute cap reached."}""")),

            UpstreamBehaviour.MalformedResponse => FakeHttpUpstream.Json(
                HttpStatusCode.Accepted,
                "not json at all"),

            UpstreamBehaviour.EchoCredentialsInError => FakeHttpUpstream.Json(
                HttpStatusCode.BadRequest,
                $$"""{"error":"bad_request","message":"Rejected request with X-Api-Key: {{ApiKey}}"}"""),

            _ => FakeHttpUpstream.Json(HttpStatusCode.Accepted, """{"message_id":"mh-0001"}"""),
        };
    }

    private static HttpResponseMessage WithRateLimitReset(HttpResponseMessage response)
    {
        response.Headers.Add(
            "X-RateLimit-Reset",
            DateTimeOffset.UtcNow.AddSeconds(45).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));

        return response;
    }

    private static bool RequestTargets(HttpRequestMessage request, string address)
    {
        string? body = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        return body?.Contains(address, StringComparison.OrdinalIgnoreCase) == true;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _upstream.Dispose();
    }
}
