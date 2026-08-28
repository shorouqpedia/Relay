using Microsoft.Extensions.Logging.Abstractions;
using Relay.Application.Delivery;
using Relay.Application.UnitTests.Builders;
using Relay.Domain.Common;
using Relay.Domain.Messaging;

namespace Relay.Application.UnitTests.Delivery;

/// <summary>
/// Provider selection, including the parts that only matter during an outage.
/// </summary>
/// <remarks>Scenario ids <c>RT01</c>–<c>RT08</c> in <c>docs/test-plan.md</c>.</remarks>
public sealed class ProviderRouterTests
{
    [Fact]
    public void RT01_Route_PrefersTheLowestPriorityNumber()
    {
        ProviderRouter router = Router(
            Profile("email.postal", ChannelType.Email, priority: 10),
            Profile("email.mailhook", ChannelType.Email, priority: 5));

        Result<ProviderProfile> chosen = router.Route(new MessageBuilder().Build());

        chosen.Value.Id.Value.ShouldBe("email.mailhook");
    }

    [Fact]
    public void RT02_Route_IgnoresProvidersForOtherChannels()
    {
        ProviderRouter router = Router(
            Profile("sms.twinkle", ChannelType.Sms, priority: 1),
            Profile("email.postal", ChannelType.Email, priority: 50));

        Result<ProviderProfile> chosen = router.Route(new MessageBuilder().Build());

        chosen.Value.Id.Value.ShouldBe("email.postal");
    }

    [Fact]
    public void RT03_Route_FailsWhenNoProviderServesTheChannel()
    {
        ProviderRouter router = Router(Profile("sms.twinkle", ChannelType.Sms, priority: 1));

        Result<ProviderProfile> chosen = router.Route(new MessageBuilder().Build());

        chosen.IsFailure.ShouldBeTrue();
        chosen.Error.Code.ShouldBe("message.no_eligible_provider");
    }

    [Fact]
    public void RT04_Route_SkipsAProviderThatIsCurrentlyUnavailable()
    {
        var health = new StubHealth();
        health.MarkUnavailable("email.mailhook");

        ProviderRouter router = Router(
            health,
            Profile("email.mailhook", ChannelType.Email, priority: 1),
            Profile("email.postal", ChannelType.Email, priority: 2));

        Result<ProviderProfile> chosen = router.Route(new MessageBuilder().Build());

        chosen.Value.Id.Value.ShouldBe("email.postal");
    }

    [Fact]
    public void RT05_Route_DoesNotReturnAProviderThisMessageAlreadyFailedOn()
    {
        ProviderRouter router = Router(
            Profile("email.postal", ChannelType.Email, priority: 1),
            Profile("email.mailhook", ChannelType.Email, priority: 2));

        Message message = new MessageBuilder().BuildAfterFailedAttempt("email.postal");

        Result<ProviderProfile> chosen = router.Route(message);

        // Without this, priority order hands the message straight back to the
        // provider that just failed it, and the retry budget is spent on one
        // upstream while a working alternative sits idle. A retry budget is only a
        // failover budget if each attempt goes somewhere new.
        chosen.Value.Id.Value.ShouldBe("email.mailhook");
    }

    [Fact]
    public void RT06_Route_FallsBackToATriedProviderWhenNothingElseRemains()
    {
        ProviderRouter router = Router(Profile("email.postal", ChannelType.Email, priority: 1));

        Message message = new MessageBuilder().BuildAfterFailedAttempt("email.postal");

        Result<ProviderProfile> chosen = router.Route(message);

        // The earlier failure may have been transient. Refusing to retry at all
        // would turn a recoverable failure into a permanent one, so exhausting the
        // alternatives is what unlocks this — not preferring it.
        chosen.IsSuccess.ShouldBeTrue();
        chosen.Value.Id.Value.ShouldBe("email.postal");
    }

    [Fact]
    public void RT07_Route_FailsWhenEveryProviderForTheChannelIsUnavailable()
    {
        var health = new StubHealth();
        health.MarkUnavailable("email.postal");
        health.MarkUnavailable("email.mailhook");

        ProviderRouter router = Router(
            health,
            Profile("email.postal", ChannelType.Email, priority: 1),
            Profile("email.mailhook", ChannelType.Email, priority: 2));

        Result<ProviderProfile> chosen = router.Route(new MessageBuilder().Build());

        // The message stays pending rather than failing. The providers are down,
        // which is a condition that resolves on its own.
        chosen.IsFailure.ShouldBeTrue();
        chosen.Error.Code.ShouldBe("message.no_eligible_provider");
    }

    [Fact]
    public void RT08_Route_PrefersAnUntriedProviderOverAHealthyTriedOne()
    {
        ProviderRouter router = Router(
            Profile("email.postal", ChannelType.Email, priority: 1),
            Profile("email.mailhook", ChannelType.Email, priority: 2));

        Message message = new MessageBuilder().BuildAfterFailedAttempt("email.postal");

        // Both are healthy and postal is preferred, but postal has already failed
        // this particular message. Untriedness beats priority.
        router.Route(message).Value.Id.Value.ShouldBe("email.mailhook");
    }

    private static ProviderRouter Router(params ProviderProfile[] providers) =>
        Router(new StubHealth(), providers);

    private static ProviderRouter Router(StubHealth health, params ProviderProfile[] providers) =>
        new(new StubRegistry(providers), health, NullLogger<ProviderRouter>.Instance);

    private static ProviderProfile Profile(string id, ChannelType channel, int priority) =>
        new(ProviderId.Create(id).Value, channel, TimeSpan.FromMinutes(30), priority);

    private sealed class StubRegistry(ProviderProfile[] providers) : IProviderRegistry
    {
        public IReadOnlyList<ProviderProfile> All => providers;

        public IReadOnlyList<ProviderProfile> For(ChannelType channel) =>
            [.. providers.Where(p => p.Channel == channel).OrderBy(p => p.Priority)];

        public ProviderProfile? Find(ProviderId id) =>
            providers.FirstOrDefault(p => p.Id == id);
    }

    private sealed class StubHealth : IProviderHealth
    {
        private readonly HashSet<string> _unavailable = new(StringComparer.Ordinal);

        public void MarkUnavailable(string id) => _unavailable.Add(id);

        public bool IsAvailable(ProviderId provider) => !_unavailable.Contains(provider.Value);

        public void Record(ProviderId provider, AttemptOutcome outcome, TimeSpan? retryAfter = null)
        {
        }
    }
}
