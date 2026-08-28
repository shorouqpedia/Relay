using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Delivery;
using Relay.Providers.Abstractions;

namespace Relay.Providers.ContractTests;

/// <summary>
/// The composed decorator chain, asserted rather than described.
/// </summary>
/// <remarks>
/// The ordering in ADR 0007 is load-bearing and invisible at every call site: a
/// refactor that reorders the registrations changes the rate-limiting semantics
/// and breaks nothing that anyone would notice. Specifically, moving the limiter
/// below the resilience decorator lets retries bypass the quota, so a provider
/// that is refusing because it is overloaded gets hit harder for refusing.
/// <para>
/// These tests are the reason that reordering fails loudly instead.
/// </para>
/// <para>Scenario ids <c>DC01</c>–<c>DC03</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
public sealed class DecoratorChainTests
{
    [Fact]
    public void DC01_DiscoveredProvider_IsWrappedNotReturnedBare()
    {
        using ServiceProvider services = Build();

        IMessageProvider resolved = services.GetServices<IMessageProvider>().First();

        // Resolving the interface must never yield a raw provider. If it did,
        // every cross-cutting concern would be silently absent for that provider
        // while the registration still looked correct.
        resolved.GetType().Name.ShouldNotBe("PostalProvider");
    }

    [Fact]
    public void DC02_TheChain_IsOrderedLoggingMetricsRateLimitResilience()
    {
        using ServiceProvider services = Build();

        IMessageProvider resolved = services.GetServices<IMessageProvider>().First();

        List<string> chain = Unwrap(resolved);

        chain.ShouldBe([
            "LoggingProviderDecorator",
            "MetricsProviderDecorator",
            "RateLimitingProviderDecorator",
            "ResilienceProviderDecorator",
            "PostalProvider",
        ]);
    }

    [Fact]
    public void DC03_TheChain_PreservesTheProviderDescriptor()
    {
        using ServiceProvider services = Build();

        IMessageProvider resolved = services.GetServices<IMessageProvider>().First();

        // Every decorator forwards the descriptor rather than inventing one. The
        // router reads it through the chain, so a decorator that answered for
        // itself would make routing address a provider that does not exist.
        resolved.Descriptor.Id.Value.ShouldBe("email.postal");
        resolved.Descriptor.Channel.ShouldBe(ChannelType.Email);
    }

    /// <summary>
    /// Walks the chain by reflection, collecting each layer's type name.
    /// </summary>
    /// <remarks>
    /// Reflection over a private field is a poor testing habit in general. It is
    /// justified here because the alternative is exposing the wrapped instance on
    /// the interface purely so a test can read it — which would let production
    /// code unwrap the chain, defeating the point of composing it.
    /// </remarks>
    private static List<string> Unwrap(IMessageProvider provider)
    {
        List<string> names = [];

        for (object? current = provider; current is not null;)
        {
            names.Add(current.GetType().Name);

            current = current.GetType()
                .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Where(f => typeof(IMessageProvider).IsAssignableFrom(f.FieldType))
                .Select(f => f.GetValue(current))
                .FirstOrDefault();
        }

        return names;
    }

    private static ServiceProvider Build()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Providers:email.postal:BaseUrl"] = "https://postal.test",
                ["Providers:email.postal:ApiKey"] = "contract-test-key",
                ["Providers:email.postal:FromAddress"] = "relay@example.com",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRelayDelivery(configuration);

        return services.BuildServiceProvider();
    }
}
