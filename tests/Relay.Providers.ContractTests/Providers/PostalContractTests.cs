using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Refit;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;
using Relay.Providers.ContractTests.Fakes;
using Relay.Providers.Email.Postal;

namespace Relay.Providers.ContractTests.Providers;

/// <summary>
/// Runs the provider contract against Postal.
/// </summary>
/// <remarks>
/// This class adds no test methods. That is the intended shape: a provider proves
/// it honours the contract by supplying an instance and a misbehaving upstream,
/// and everything asserted about it comes from <see cref="ProviderContract"/>.
/// A provider that needed its own version of one of those assertions would be
/// telling us the contract is not actually shared.
/// </remarks>
public sealed class PostalContractTests : ProviderContract, IDisposable
{
    private const string ApiKey = IControllableUpstream.SecretMarker;

    private readonly FakeHttpUpstream _upstream;
    private readonly HttpClient _httpClient;
    private readonly PostalProviderHandle _handle;

    public PostalContractTests()
    {
        _upstream = new FakeHttpUpstream(Respond);

        _httpClient = new HttpClient(_upstream)
        {
            BaseAddress = new Uri("https://postal.test"),

            // Short enough that PC08 finishes quickly, long enough that a loaded
            // CI agent does not trip it during the scenarios that should succeed.
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        _handle = PostalProviderHandle.Create(_httpClient, ApiKey);
    }

    protected override IMessageProvider Provider => _handle.Provider;

    protected override IControllableUpstream Upstream => _upstream;

    protected override DeliveryRequest ValidRequest() => Request("recipient@example.com");

    protected override DeliveryRequest PermanentlyRejectedRequest() => Request(BlockedAddress);

    /// <summary>An address the fake upstream refuses outright, whatever else it is doing.</summary>
    private const string BlockedAddress = "suppressed@example.com";

    private static DeliveryRequest Request(string address) => new(
        MessageId.New(),
        Recipient.Create(ChannelType.Email, address).Value,
        MessageBody.Create(ChannelType.Email, "Body text.", "Subject").Value,
        IdempotencyKey.Create("contract-test-key-01").Value,
        Attempt: 1);

    private static HttpResponseMessage Respond(UpstreamBehaviour behaviour, HttpRequestMessage request)
    {
        // A permanent refusal is a property of the recipient, not of the chosen
        // behaviour, so it is checked first — that is what makes PC04 meaningful
        // while the upstream is otherwise healthy.
        if (RequestTargets(request, BlockedAddress))
        {
            return FakeHttpUpstream.Json(
                HttpStatusCode.UnprocessableEntity,
                """{"error":"suppressed_recipient","message":"This address is on the suppression list."}""");
        }

        return behaviour switch
        {
            UpstreamBehaviour.Accept => FakeHttpUpstream.Json(
                HttpStatusCode.Accepted,
                """{"id":"postal-msg-0001"}"""),

            UpstreamBehaviour.ServerError => FakeHttpUpstream.Json(
                HttpStatusCode.BadGateway,
                """{"error":"upstream_unavailable","message":"Try again shortly."}"""),

            UpstreamBehaviour.RateLimit => FakeHttpUpstream.Json(
                HttpStatusCode.TooManyRequests,
                """{"error":"rate_limited","message":"Hourly quota reached."}""",
                retryAfter: TimeSpan.FromSeconds(30)),

            UpstreamBehaviour.MalformedResponse => FakeHttpUpstream.Json(
                HttpStatusCode.Accepted,
                "<html><body>502 Bad Gateway</body></html>"),

            UpstreamBehaviour.EchoCredentialsInError => FakeHttpUpstream.Json(
                HttpStatusCode.BadRequest,
                $$"""{"error":"bad_request","message":"Rejected request with Authorization: Bearer {{ApiKey}}"}"""),

            // Hang is handled by the fake before it gets here.
            _ => FakeHttpUpstream.Json(HttpStatusCode.Accepted, """{"id":"postal-msg-0001"}"""),
        };
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

    /// <summary>
    /// Builds a <c>PostalProvider</c> over a supplied <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// <c>PostalProvider</c> and its Refit interface are internal to the provider
    /// assembly, which is correct — nothing outside it should construct one. The
    /// assembly grants this test project access via <c>InternalsVisibleTo</c>
    /// rather than widening the type, so the encapsulation holds in production and
    /// gives way only for the suite that verifies it.
    /// </remarks>
    private sealed record PostalProviderHandle(IMessageProvider Provider)
    {
        public static PostalProviderHandle Create(HttpClient client, string apiKey)
        {
            IPostalApi api = RestService.For<IPostalApi>(client);

            var options = Options.Create(new PostalOptions
            {
                BaseUrl = "https://postal.test",
                ApiKey = apiKey,
                FromAddress = "relay@example.com",
                Timeout = TimeSpan.FromMilliseconds(500),
            });

            return new PostalProviderHandle(
                new PostalProvider(api, options, NullLogger<PostalProvider>.Instance));
        }
    }
}
