using System.Net;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using Relay.Providers.ContractTests.Fakes;
using Relay.Providers.Sms.Twinkle;

namespace Relay.Providers.ContractTests.Providers;

/// <summary>
/// Runs the provider contract against Twinkle.
/// </summary>
/// <remarks>
/// The most valuable subclass in the suite, because Twinkle answers <b>HTTP 200
/// for everything</b> — rejections, rate limits, and internal errors all arrive
/// with a success status and the real outcome in the body.
/// <para>
/// Every scenario below except the timeout is set up with a 200, so a provider
/// that trusted the status line would report every one of them as accepted and
/// pass none of the assertions. That is the case the contract suite exists to
/// make impossible to ship.
/// </para>
/// </remarks>
public sealed class TwinkleContractTests : ProviderContract, IDisposable
{
    private const string ApiKey = IControllableUpstream.SecretMarker;
    private const string BlockedNumber = "+201009999999";

    private readonly FakeHttpUpstream _upstream;
    private readonly HttpClient _httpClient;
    private readonly TwinkleProvider _provider;

    public TwinkleContractTests()
    {
        _upstream = new FakeHttpUpstream(Respond);

        _httpClient = new HttpClient(_upstream)
        {
            BaseAddress = new Uri("https://twinkle.test"),
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        _provider = new TwinkleProvider(
            _httpClient,
            Options.Create(new TwinkleOptions
            {
                BaseUrl = "https://twinkle.test",
                AccountId = "acct-test",
                ApiKey = ApiKey,
                SenderId = "Relay",
                Timeout = TimeSpan.FromMilliseconds(500),
            }));
    }

    protected override IMessageProvider Provider => _provider;

    protected override IControllableUpstream Upstream => _upstream;

    protected override DeliveryRequest ValidRequest() => Request("+201001234567");

    protected override DeliveryRequest PermanentlyRejectedRequest() => Request(BlockedNumber);

    private static DeliveryRequest Request(string number) => new(
        MessageId.New(),
        Recipient.Create(ChannelType.Sms, number).Value,
        MessageBody.Create(ChannelType.Sms, "Body text.").Value,
        IdempotencyKey.Create("contract-test-key-03").Value,
        Attempt: 1);

    private static HttpResponseMessage Respond(UpstreamBehaviour behaviour, HttpRequestMessage request)
    {
        // Note the status code on every branch below.
        if (RequestTargets(request, BlockedNumber))
        {
            return FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"status":"blocked","detail":"This number has opted out."}""");
        }

        return behaviour switch
        {
            UpstreamBehaviour.Accept => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"status":"queued","message_id":"tw-0001"}"""),

            UpstreamBehaviour.ServerError => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"status":"carrier_unavailable","detail":"Upstream carrier is not responding."}"""),

            UpstreamBehaviour.RateLimit => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"status":"rate_limited","detail":"Throughput cap reached.","retry_after":20}"""),

            UpstreamBehaviour.MalformedResponse => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                "<xml>this is not the API you are looking for</xml>"),

            UpstreamBehaviour.EchoCredentialsInError => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                $$"""{"status":"internal_error","detail":"Auth failed for token {{ApiKey}}"}"""),

            _ => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"status":"queued","message_id":"tw-0001"}"""),
        };
    }

    private static bool RequestTargets(HttpRequestMessage request, string number)
    {
        string? body = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Form-encoded, so the plus sign is escaped. Matching the tail avoids
        // depending on how the encoder chose to spell it.
        return body?.Contains(number.TrimStart('+'), StringComparison.Ordinal) == true;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _upstream.Dispose();
    }
}
