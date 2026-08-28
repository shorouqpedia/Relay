using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.ContractTests;

/// <summary>
/// The behaviour every provider must exhibit, asserted once and run against each
/// provider assembly.
/// </summary>
/// <remarks>
/// Each provider adds a sealed subclass that supplies an instance and a way to
/// make its upstream misbehave. It adds nothing else. If a provider cannot pass
/// this suite, the conclusion is that the abstraction is wrong — not that the
/// interface needs an opt-out flag. A capability that only some providers have
/// belongs in <see cref="ProviderCapabilities"/>, where the router can see it,
/// rather than in a special case here.
/// <para>
/// Most of the assertions are about the unhappy half of the contract, because
/// that is the half that is easy to get wrong and impossible to notice: a
/// provider that leaks a vendor exception works perfectly until the day its
/// upstream has an outage.
/// </para>
/// <para>Scenario ids <c>PC01</c>–<c>PC12</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
public abstract class ProviderContract
{
    /// <summary>The provider under test, wired to <see cref="Upstream"/>.</summary>
    protected abstract IMessageProvider Provider { get; }

    /// <summary>
    /// The controllable stand-in for this provider's upstream.
    /// </summary>
    /// <remarks>
    /// A fake rather than a mock. The behaviours being driven here — a timeout, a
    /// 429 with a Retry-After, a malformed body — are stateful and are asserted
    /// against repeatedly, and expressing them as mock setups would put the
    /// scenario in the test instead of in the fake.
    /// </remarks>
    protected abstract IControllableUpstream Upstream { get; }

    /// <summary>A request this provider's channel accepts.</summary>
    protected abstract DeliveryRequest ValidRequest();

    /// <summary>
    /// A request that is well-formed for the channel but that the upstream will
    /// refuse permanently — a suppressed recipient, a blocked number.
    /// </summary>
    protected abstract DeliveryRequest PermanentlyRejectedRequest();

    [Fact]
    public void PC01_Descriptor_IdMatchesTheChannelItServes()
    {
        ProviderDescriptor descriptor = Provider.Descriptor;

        descriptor.Channel.ShouldNotBe(ChannelType.None);
        descriptor.Id.Value.ShouldNotBeNullOrWhiteSpace();
        descriptor.ExpectedReceiptWindow.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task PC02_Send_WhenUpstreamAccepts_ReportsAccepted()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AttemptOutcome.Accepted);
        result.FailureReason.ShouldBeNull();
    }

    [Fact]
    public async Task PC03_Send_WhenAcceptedAndCapabilityDeclared_ReturnsAMessageId()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Declaring ReturnsMessageId and then not returning one would make the
        // message unreconcilable while looking reconcilable to the router.
        if (Provider.Descriptor.Supports(ProviderCapabilities.ReturnsMessageId))
        {
            result.ProviderMessageId.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task PC04_Send_WhenUpstreamRefusesPermanently_ReportsRejected()
    {
        Upstream.Behave(UpstreamBehaviour.Accept);

        DeliveryResult result = await Provider.SendAsync(
            PermanentlyRejectedRequest(), TestContext.Current.CancellationToken);

        // Rejected rather than TransientFailure: this is the classification that
        // decides whether the message burns its entire retry budget re-asking a
        // question already answered.
        result.Outcome.ShouldBe(AttemptOutcome.Rejected);
        result.FailureReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PC05_Send_WhenUpstreamIsUnavailable_ReportsTransientFailure()
    {
        Upstream.Behave(UpstreamBehaviour.ServerError);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        result.Outcome.ShouldBe(AttemptOutcome.TransientFailure);
    }

    [Fact]
    public async Task PC06_Send_WhenUpstreamRateLimits_ReportsRateLimited()
    {
        Upstream.Behave(UpstreamBehaviour.RateLimit);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Every upstream expresses this differently — 429, or 403, or a 200 with
        // an error code in the body. Collapsing them to one outcome is the
        // provider's whole job.
        result.Outcome.ShouldBe(AttemptOutcome.RateLimited);
    }

    [Fact]
    public async Task PC07_Send_WhenRateLimitedAndCapabilityDeclared_ReportsRetryAfter()
    {
        Upstream.Behave(UpstreamBehaviour.RateLimit);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        if (Provider.Descriptor.Supports(ProviderCapabilities.ReportsRetryAfter))
        {
            result.RetryAfter.ShouldNotBeNull();
            result.RetryAfter.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task PC08_Send_WhenUpstreamNeverResponds_ReportsTimeout()
    {
        Upstream.Behave(UpstreamBehaviour.Hang);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Timeout, not TransientFailure. The distinction is that the message may
        // already have been sent, which is what makes the retry a decision to risk
        // a duplicate rather than a free action (ADR 0008).
        result.Outcome.ShouldBe(AttemptOutcome.Timeout);
    }

    [Fact]
    public async Task PC09_Send_WhenUpstreamReturnsNonsense_DoesNotThrow()
    {
        Upstream.Behave(UpstreamBehaviour.MalformedResponse);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // The requirement is that a provider survives a response it did not
        // expect. A provider that lets a deserialization exception escape works
        // perfectly until the day its upstream ships a bad body, and then takes
        // down a caller that had no way to catch it.
        //
        // Which outcome it reports is deliberately not specified. This assertion
        // used to demand TransientFailure, which quietly assumed every provider
        // reads a body to decide whether it succeeded. The webhook provider does
        // not — it posts to an endpoint the recipient supplied, and the status
        // line is the whole answer — so for it, a 200 carrying an unparseable
        // body is a genuine success and reporting a failure would be the bug.
        //
        // That assumption was invisible until a provider arrived that broke it.
        result.Outcome.ShouldNotBe(AttemptOutcome.None);
    }

    [Fact]
    public async Task PC10_Send_WhenCancelled_ThrowsOperationCanceled()
    {
        Upstream.Behave(UpstreamBehaviour.Hang);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // The one exception the contract permits. Swallowing it and returning a
        // result would make the pipeline unstoppable during a shutdown.
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await Provider.SendAsync(ValidRequest(), cts.Token));
    }

    [Fact]
    public async Task PC11_Send_FailureReason_DoesNotLeakCredentials()
    {
        Upstream.Behave(UpstreamBehaviour.EchoCredentialsInError);

        DeliveryResult result = await Provider.SendAsync(ValidRequest(), TestContext.Current.CancellationToken);

        // Failure reasons are logged and are shown to operators. An upstream that
        // echoes the API key back in an error message is not unusual, and a
        // provider that passes it through has put a credential in the log store.
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason.ShouldNotContain(
            IControllableUpstream.SecretMarker,
            Case.Insensitive);
    }

    [Fact]
    public void PC12_ReceiptQuery_CapabilityAndImplementationAgree()
    {
        bool declares = Provider.Descriptor.Supports(ProviderCapabilities.QueryableReceipts);
        bool implements = Provider is IReceiptQueryable;

        // Checked in both directions. Declaring without implementing makes the
        // sweeper call something that is not there; implementing without
        // declaring means the sweeper never asks, and the capability is dead code
        // that looks like a feature.
        declares.ShouldBe(implements);
    }
}
