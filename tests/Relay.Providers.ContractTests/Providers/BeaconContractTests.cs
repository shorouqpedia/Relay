using System.Net;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using Relay.Providers.ContractTests.Fakes;
using Relay.Providers.Push.Beacon;

namespace Relay.Providers.ContractTests.Providers;

/// <summary>
/// Runs the provider contract against Beacon.
/// </summary>
/// <remarks>
/// Beacon declares one capability and implements no receipt query at all, so
/// several scenarios reduce to nothing here: <c>PC07</c> skips its assertion
/// because <c>ReportsRetryAfter</c> is absent, and <c>PC12</c> passes by
/// confirming that the missing capability and the missing implementation agree.
/// <para>
/// That is the intended shape. The suite adapts to what a provider claims rather
/// than demanding every provider be the same, and it still catches the failure
/// that matters — a descriptor promising something the code does not do.
/// </para>
/// </remarks>
public sealed class BeaconContractTests : ProviderContract, IDisposable
{
    private const string ApiKey = IControllableUpstream.SecretMarker;
    private const string RetiredToken = "d3adb33fd3adb33fd3ad";

    private readonly FakeHttpUpstream _upstream;
    private readonly HttpClient _httpClient;
    private readonly BeaconProvider _provider;

    public BeaconContractTests()
    {
        _upstream = new FakeHttpUpstream(Respond);

        _httpClient = new HttpClient(_upstream)
        {
            BaseAddress = new Uri("https://beacon.test"),
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        _provider = new BeaconProvider(
            _httpClient,
            Options.Create(new BeaconOptions
            {
                BaseUrl = "https://beacon.test",
                ApiKey = ApiKey,
                DefaultTitle = "Relay",
                Timeout = TimeSpan.FromMilliseconds(500),
            }));
    }

    protected override IMessageProvider Provider => _provider;

    protected override IControllableUpstream Upstream => _upstream;

    protected override DeliveryRequest ValidRequest() => Request("a1b2c3d4e5f60718293a");

    /// <inheritdoc />
    /// <remarks>
    /// A device token the platform has retired. Permanent in a way an email
    /// address is not: the token does not come back, and the message can only be
    /// delivered again once the app re-registers and supplies a new one.
    /// </remarks>
    protected override DeliveryRequest PermanentlyRejectedRequest() => Request(RetiredToken);

    [Fact]
    public async Task PC09a_Send_WhenTheBodyCannotBeParsed_ReportsATransientFailure()
    {
        Upstream.Behave(UpstreamBehaviour.MalformedResponse);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Sharper than PC09, and stated here rather than in the shared contract
        // because it is only true of providers that read a body to decide whether
        // they succeeded. Beacon does; the webhook provider does not.
        //
        // Reporting success on an unreadable body would record a message as sent
        // on the strength of a response nobody could interpret.
        result.Outcome.ShouldBe(AttemptOutcome.TransientFailure);
    }

    private static DeliveryRequest Request(string token) => new(
        MessageId.New(),
        Recipient.Create(ChannelType.Push, token).Value,
        MessageBody.Create(ChannelType.Push, "Body text.", "Title").Value,
        IdempotencyKey.Create("contract-test-key-04").Value,
        Attempt: 1);

    private static HttpResponseMessage Respond(UpstreamBehaviour behaviour, HttpRequestMessage request)
    {
        if (RequestTargets(request, RetiredToken))
        {
            return FakeHttpUpstream.Json(
                HttpStatusCode.Gone,
                """{"error":"token_unregistered"}""");
        }

        return behaviour switch
        {
            UpstreamBehaviour.Accept => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                """{"id":"bc-0001"}"""),

            UpstreamBehaviour.ServerError => FakeHttpUpstream.Json(
                HttpStatusCode.InternalServerError,
                """{"error":"internal"}"""),

            // No Retry-After header and no equivalent in the body. The provider
            // does not claim ReportsRetryAfter, so the contract does not ask for
            // one — and the caller falls back to its configured backoff.
            UpstreamBehaviour.RateLimit => FakeHttpUpstream.Json(
                HttpStatusCode.TooManyRequests,
                """{"error":"quota_exceeded"}"""),

            UpstreamBehaviour.MalformedResponse => FakeHttpUpstream.Json(
                HttpStatusCode.OK,
                "{ truncated"),

            UpstreamBehaviour.EchoCredentialsInError => FakeHttpUpstream.Json(
                HttpStatusCode.BadRequest,
                $$"""{"error":"invalid auth header Bearer {{ApiKey}}"}"""),

            _ => FakeHttpUpstream.Json(HttpStatusCode.OK, """{"id":"bc-0001"}"""),
        };
    }

    private static bool RequestTargets(HttpRequestMessage request, string token)
    {
        string? body = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
        return body?.Contains(token, StringComparison.Ordinal) == true;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _upstream.Dispose();
    }
}
