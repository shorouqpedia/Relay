using System.Net;
using System.Net.Http.Headers;

namespace Relay.Providers.ContractTests.Fakes;

/// <summary>
/// An HTTP upstream that misbehaves on request.
/// </summary>
/// <remarks>
/// A hand-written <see cref="HttpMessageHandler"/> rather than a mocking library.
/// The behaviours here are stateful and are driven from many tests, and expressing
/// them as per-test mock setups would scatter one upstream's semantics across the
/// suite instead of keeping it in one readable place.
/// <para>
/// Response bodies differ per provider, so each provider supplies its own through
/// <paramref name="respond"/>. The behaviours do not differ, which is what lets
/// the contract suite assert the same twelve scenarios against every provider.
/// </para>
/// </remarks>
/// <param name="respond">Builds this provider's response for a given behaviour.</param>
internal sealed class FakeHttpUpstream(
    Func<UpstreamBehaviour, HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler, IControllableUpstream
{
    private volatile UpstreamBehaviour _behaviour = UpstreamBehaviour.Accept;

    /// <summary>How many requests have reached the upstream.</summary>
    /// <remarks>
    /// Used by tests that care whether a retry actually produced a second physical
    /// call — the question the decorator ordering in ADR 0007 turns on.
    /// </remarks>
    public int RequestCount { get; private set; }

    public void Behave(UpstreamBehaviour behaviour) => _behaviour = behaviour;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;

        if (_behaviour is UpstreamBehaviour.Hang)
        {
            // Never answers. The caller's own timeout is what ends this, which is
            // precisely the condition a provider must report as Timeout rather
            // than let escape as cancellation.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage response = respond(_behaviour, request);

        // A real handler sets this, and Refit reads it when building the error it
        // attaches to a non-success response. Omitting it turns every failure
        // scenario into an InvalidOperationException from inside the client.
        response.RequestMessage = request;

        return response;
    }

    /// <summary>
    /// Builds a response carrying <paramref name="body"/>, with a Retry-After
    /// header when the status calls for one.
    /// </summary>
    public static HttpResponseMessage Json(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };

        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }
}
