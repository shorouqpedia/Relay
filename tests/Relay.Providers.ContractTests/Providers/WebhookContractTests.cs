using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using Relay.Providers.ContractTests.Fakes;
using Relay.Providers.Webhook;

namespace Relay.Providers.ContractTests.Providers;

/// <summary>
/// Runs the provider contract against the webhook provider.
/// </summary>
/// <remarks>
/// The provider with the weakest capability set in the system, which makes it the
/// sharpest test of whether the contract accommodates difference or merely
/// tolerates similarity. <c>PC03</c>, <c>PC07</c>, and <c>PC12</c> all reduce to
/// checking that nothing was promised, and they pass for that reason rather than
/// by accident.
/// <para>
/// The signature assertions below are not part of the shared contract — no other
/// provider signs anything — so they live here, which is where a provider's own
/// peculiarities belong.
/// </para>
/// </remarks>
public sealed class WebhookContractTests : ProviderContract, IDisposable
{
    private const string SigningSecret = "contract-test-signing-secret-0123456789";
    private const string RefusingEndpoint = "https://receiver.test/refuses";

    private readonly FakeHttpUpstream _upstream;
    private readonly HttpClient _httpClient;
    private readonly WebhookProvider _provider;

    /// <summary>
    /// What the receiver saw, captured as the fake responds.
    /// </summary>
    /// <remarks>
    /// Recorded here rather than added to <c>FakeHttpUpstream</c>, because only
    /// this provider's tests care what the outbound request looked like — the
    /// others assert on the result, not on the call. The body is captured eagerly
    /// because the content is disposed once the response is handled.
    /// </remarks>
    private HttpRequestMessage? _lastRequest;
    private string? _lastBody;

    public WebhookContractTests()
    {
        _upstream = new FakeHttpUpstream(Respond);

        // No BaseAddress: the destination comes from the recipient, which is the
        // whole shape of this provider.
        _httpClient = new HttpClient(_upstream) { Timeout = TimeSpan.FromMilliseconds(500) };

        _provider = new WebhookProvider(
            _httpClient,
            Options.Create(new WebhookOptions
            {
                SigningSecret = SigningSecret,
                Timeout = TimeSpan.FromMilliseconds(500),
            }));
    }

    protected override IMessageProvider Provider => _provider;

    protected override IControllableUpstream Upstream => _upstream;

    protected override DeliveryRequest ValidRequest() => Request("https://receiver.test/inbound");

    protected override DeliveryRequest PermanentlyRejectedRequest() => Request(RefusingEndpoint);

    [Fact]
    public async Task WH01_Send_SignsThePayloadSoAReceiverCanVerifyIt()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        HttpRequestMessage sent = _lastRequest.ShouldNotBeNull();
        string body = _lastBody.ShouldNotBeNull();

        string timestamp = sent.Headers.GetValues("X-Relay-Timestamp").Single();
        string signature = sent.Headers.GetValues("X-Relay-Signature").Single();

        // Recomputed the way a receiver would, over timestamp and payload rather
        // than payload alone. Signing the body on its own would produce a
        // signature that stays valid forever, so anyone who saw one request could
        // replay it indefinitely.
        byte[] expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SigningSecret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}"));

        signature.ShouldBe($"sha256={Convert.ToHexStringLower(expected)}");
    }

    [Fact]
    public async Task WH02_Send_CarriesTheMessageIdSoAReceiverCanDeduplicate()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        DeliveryRequest request = ValidRequest();
        await Provider.SendAsync(request, TestContext.Current.CancellationToken);

        // Relay delivers at least once, so the same webhook can legitimately
        // arrive twice. The receiver needs a key to deduplicate on, in a header
        // so it can do so without parsing the body.
        _lastRequest!.Headers.GetValues("X-Relay-Message-Id").Single()
            .ShouldBe(request.MessageId.Value.ToString());
    }

    [Fact]
    public async Task WH03_Send_PostsToTheRecipientAddressItself()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        _lastRequest!.RequestUri!.ToString().ShouldBe("https://receiver.test/inbound");
    }

    private static DeliveryRequest Request(string endpoint) => new(
        MessageId.New(),
        Recipient.Create(ChannelType.Webhook, endpoint).Value,
        MessageBody.Create(ChannelType.Webhook, """{"event":"test"}""").Value,
        IdempotencyKey.Create("contract-test-key-05").Value,
        Attempt: 1);

    private HttpResponseMessage Respond(UpstreamBehaviour behaviour, HttpRequestMessage request)
    {
        _lastRequest = request;
        _lastBody = request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();

        if (request.RequestUri?.ToString() == RefusingEndpoint)
        {
            return FakeHttpUpstream.Json(HttpStatusCode.UnprocessableEntity, """{"error":"rejected"}""");
        }

        return behaviour switch
        {
            UpstreamBehaviour.Accept => new HttpResponseMessage(HttpStatusCode.NoContent),

            UpstreamBehaviour.ServerError => FakeHttpUpstream.Json(
                HttpStatusCode.BadGateway, """{"error":"upstream"}"""),

            UpstreamBehaviour.RateLimit => WithRetryAfter(
                FakeHttpUpstream.Json(HttpStatusCode.TooManyRequests, """{"error":"slow down"}""")),

            // A receiver returning a success status with a body it did not
            // promise. Harmless here, because this provider never reads the body —
            // the status is the entire answer.
            UpstreamBehaviour.MalformedResponse => FakeHttpUpstream.Json(
                HttpStatusCode.OK, "<html>ok?</html>"),

            // Nothing to leak: the signing secret is never sent, only used to
            // derive a signature, so a receiver cannot echo it back.
            UpstreamBehaviour.EchoCredentialsInError => FakeHttpUpstream.Json(
                HttpStatusCode.BadGateway, """{"error":"gateway timeout talking to origin"}"""),

            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };
    }

    private static HttpResponseMessage WithRetryAfter(HttpResponseMessage response)
    {
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(15));
        return response;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _upstream.Dispose();
    }
}
