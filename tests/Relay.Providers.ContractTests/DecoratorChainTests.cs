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
    public void DC01_EveryDiscoveredProvider_IsWrappedNotReturnedBare()
    {
        using ServiceProvider services = Build();

        IMessageProvider[] resolved = [.. services.GetServices<IMessageProvider>()];

        resolved.ShouldNotBeEmpty();

        // Asserted for every provider, not a sample. A registration that bypasses
        // the chain leaves that one provider with no rate limiting, no metrics and
        // no retries, while the registration still reads correctly — so the check
        // has to cover whichever provider got it wrong.
        foreach (IMessageProvider provider in resolved)
        {
            Unwrap(provider).Count.ShouldBeGreaterThan(1);
        }
    }

    [Fact]
    public void DC02_TheChain_IsOrderedLoggingMetricsRateLimitResilience()
    {
        using ServiceProvider services = Build();

        foreach (IMessageProvider provider in services.GetServices<IMessageProvider>())
        {
            List<string> chain = Unwrap(provider);

            // The decorators, in order, then whatever the concrete provider is
            // called. Checked per provider because the chain is composed per
            // registration — one provider can be wrapped differently from another
            // without anything else noticing.
            chain[..4].ShouldBe(
                [
                    "LoggingProviderDecorator",
                    "MetricsProviderDecorator",
                    "RateLimitingProviderDecorator",
                    "ResilienceProviderDecorator",
                ],
                customMessage: $"Chain for {provider.Descriptor.Id} was [{string.Join(" → ", chain)}]");

            chain.Count.ShouldBe(5);
        }
    }

    [Fact]
    public void DC03_TheChain_PreservesEachProviderDescriptor()
    {
        using ServiceProvider services = Build();

        IMessageProvider[] resolved = [.. services.GetServices<IMessageProvider>()];

        // Every decorator forwards the descriptor rather than inventing one. The
        // router reads it through the chain, so a decorator that answered for
        // itself would make routing address a provider that does not exist.
        resolved.Select(p => p.Descriptor.Id.Value).ShouldContain("email.postal");
        resolved.Select(p => p.Descriptor.Id.Value).Distinct().Count().ShouldBe(resolved.Length);

        foreach (IMessageProvider provider in resolved)
        {
            provider.Descriptor.Channel.ShouldNotBe(ChannelType.None);
        }
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

    /// <summary>
    /// Builds the real container with every discovered provider configured.
    /// </summary>
    /// <remarks>
    /// Configuration is generated from the discovered modules rather than written
    /// out per provider, and that is not laziness — it is what keeps these tests
    /// from contradicting the property they sit next to. A hand-written list here
    /// would mean adding a provider requires editing this file, so the zero-edit
    /// claim in ADR 0003 would hold for <c>src/</c> and quietly fail in
    /// <c>tests/</c>. This file found that out the first time three providers were
    /// added at once.
    /// <para>
    /// The key set is the union of what the providers require. Binding ignores
    /// keys an options class does not have, so a provider needing only three of
    /// them is unaffected by the other three being present.
    /// </para>
    /// </remarks>
    private static ServiceProvider Build()
    {
        Dictionary<string, string?> settings = [];

        foreach (string id in DiscoverProviderIds())
        {
            settings[$"Providers:{id}:BaseUrl"] = $"https://{id}.test";
            settings[$"Providers:{id}:ApiKey"] = "contract-test-key";
            settings[$"Providers:{id}:FromAddress"] = "relay@example.com";
            settings[$"Providers:{id}:AccountId"] = "acct-test";
            settings[$"Providers:{id}:SenderId"] = "Relay";
            settings[$"Providers:{id}:DefaultTitle"] = "Relay";
            settings[$"Providers:{id}:SigningSecret"] = "contract-test-signing-secret";
        }

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRelayDelivery(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The ids of every provider module in the loaded assemblies.
    /// </summary>
    /// <remarks>
    /// Found the same way the host finds them, so the set the container is
    /// configured for is by construction the set it will discover.
    /// </remarks>
    private static IEnumerable<string> DiscoverProviderIds() =>
        AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type =>
                typeof(IProviderModule).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false }
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static type => ((IProviderModule)Activator.CreateInstance(type)!).Descriptor.Id.Value);
}
