using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Delivery.Decorators;
using Relay.Providers.Abstractions;

namespace Relay.Infrastructure.Delivery;

/// <summary>
/// Discovers providers and wires the delivery pipeline.
/// </summary>
public static class DeliveryServiceCollectionExtensions
{
    /// <summary>
    /// Registers routing, health tracking, the gateway, and every discovered provider.
    /// </summary>
    public static IServiceCollection AddRelayDelivery(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RoutingOptions>(configuration.GetSection(RoutingOptions.SectionName));
        services.Configure<ProviderHealthOptions>(
            configuration.GetSection(ProviderHealthOptions.SectionName));
        services.Configure<ProviderRateLimitOptions>(
            configuration.GetSection(ProviderRateLimitOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.AddMetrics();
        services.AddSingleton<DeliveryMetrics>();

        services.AddSingleton<IProviderHealth, ProviderHealthTracker>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        services.AddScoped<DeliveryGateway>();
        services.AddScoped<IDeliveryGateway>(sp => sp.GetRequiredService<DeliveryGateway>());
        services.AddScoped<IReceiptQuery>(sp => sp.GetRequiredService<DeliveryGateway>());

        services.AddScoped<Relay.Application.Callbacks.ICallbackGateway, Callbacks.CallbackGateway>();
        services.AddScoped<Relay.Application.Callbacks.ICallbackLog, Callbacks.CallbackLog>();

        services.AddScoped<ProviderRouter>();
        services.AddScoped<MessageDispatcher>();
        services.AddScoped<ReceiptReconciler>();
        services.AddScoped<StuckDispatchRecovery>();

        AddDiscoveredProviders(services, configuration);

        return services;
    }

    /// <summary>
    /// Finds every provider module in the loaded assemblies and registers it.
    /// </summary>
    /// <remarks>
    /// The mechanism behind ADR 0003. Nothing here names a provider, so adding one
    /// changes no file in this assembly or in either host.
    /// <para>
    /// Discovery is by convention, and convention fails quietly — a module in an
    /// assembly the host never loads is simply absent, with no error. Two things
    /// compensate: the set that was found is logged at startup, and a duplicate
    /// provider id throws rather than letting one registration silently shadow the
    /// other.
    /// </para>
    /// </remarks>
    private static void AddDiscoveredProviders(
        IServiceCollection services,
        IConfiguration configuration)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (IProviderModule module in DiscoverModules())
        {
            string id = module.Descriptor.Id.Value;

            if (!seen.Add(id))
            {
                // Two assemblies claiming one id. Left alone, the container would
                // register both and routing would pick whichever the enumeration
                // happened to yield first — a bug that reproduces intermittently
                // and only under load.
                throw new InvalidOperationException(
                    $"Two provider modules both declare the id '{id}'. "
                    + "Provider ids must be unique across assemblies.");
            }

            // Each module reads its own settings and cannot see another's, because
            // it is handed the section rather than the root.
            IConfiguration section = configuration.GetSection($"Providers:{id}");

            module.Register(services, section);
        }

        DecorateProviders(services);
    }

    private static IEnumerable<IProviderModule> DiscoverModules()
    {
        // Provider assemblies may not be loaded yet — the CLR loads lazily, and
        // nothing has touched their types at this point. Loading them explicitly is
        // what makes the wildcard reference in the host .csproj reach the runtime.
        LoadProviderAssemblies();

        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic)
            .SelectMany(GetLoadableTypes)
            .Where(static type =>
                typeof(IProviderModule).IsAssignableFrom(type)
                && type is { IsAbstract: false, IsInterface: false }
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .Select(static type => (IProviderModule)Activator.CreateInstance(type)!)
            .OrderBy(static module => module.Descriptor.Id.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads every provider assembly sitting next to the host.
    /// </summary>
    /// <remarks>
    /// The deployment directory, not <c>Assembly.GetEntryAssembly()</c>.
    /// <para>
    /// The entry assembly is whatever started the process, which is the host only
    /// when the host was launched directly. Under a test runner it is the runner;
    /// under some launchers it is null. Reading its references therefore found no
    /// providers whenever the host was hosted rather than run — and the failure
    /// was silent, because discovery finding nothing looks exactly like there
    /// being nothing to find. The integration suite caught it as a 404 from the
    /// callback endpoint, several layers away from the cause.
    /// </para>
    /// <para>
    /// Scanning the directory also matches what the design claims: providers are
    /// assemblies that sit alongside the host, and adding one is adding a file.
    /// </para>
    /// </remarks>
    private static void LoadProviderAssemblies()
    {
        foreach (string path in Directory.EnumerateFiles(
                     AppContext.BaseDirectory,
                     "Relay.Providers.*.dll"))
        {
            try
            {
                // Loading an already-loaded assembly returns the existing instance
                // rather than a second copy, so this is safe to call repeatedly —
                // which matters because a test host builds the container more than
                // once per process.
                Assembly.LoadFrom(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
            {
                // A file matching the name pattern that is not a loadable managed
                // assembly — a native dependency, a partial copy from a
                // half-finished publish. Skipped rather than fatal: one bad file
                // must not stop the providers that did load, and a provider that
                // genuinely failed to load shows up as absent in the startup log.
            }
        }
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // One unloadable type in an unrelated assembly must not stop provider
            // discovery. Returning what did load is strictly better than returning
            // nothing, and the modules we care about are in assemblies that load
            // cleanly or fail loudly elsewhere.
            return exception.Types.OfType<Type>();
        }
    }

    /// <summary>
    /// Wraps every registered provider in the decorator chain.
    /// </summary>
    /// <remarks>
    /// Applied here, once, rather than by each module — so every provider gets the
    /// same treatment and a module cannot opt out of, or reorder, the concerns
    /// that make the system predictable.
    /// <para>
    /// The composed order, outermost first, is Logging → Metrics → RateLimit →
    /// Resilience → provider. Registration runs innermost first, so this method
    /// reads in the reverse of the order things execute in. The reasoning for the
    /// order is in ADR 0007, and it is asserted by a test rather than left to this
    /// comment.
    /// </para>
    /// </remarks>
    private static void DecorateProviders(IServiceCollection services)
    {
        List<ServiceDescriptor> registrations = [.. services
            .Where(static d => d.ServiceType == typeof(IMessageProvider))];

        foreach (ServiceDescriptor registration in registrations)
        {
            services.Remove(registration);
            services.Add(new ServiceDescriptor(
                typeof(IMessageProvider),
                sp => Decorate(sp, Resolve(sp, registration)),
                registration.Lifetime));
        }
    }

    private static IMessageProvider Resolve(IServiceProvider sp, ServiceDescriptor registration)
    {
        if (registration.ImplementationFactory is { } factory)
        {
            return (IMessageProvider)factory(sp);
        }

        if (registration.ImplementationInstance is IMessageProvider instance)
        {
            return instance;
        }

        return (IMessageProvider)ActivatorUtilities.CreateInstance(
            sp,
            registration.ImplementationType!);
    }

    private static IMessageProvider Decorate(IServiceProvider sp, IMessageProvider provider)
    {
        ILoggerFactory loggers = sp.GetRequiredService<ILoggerFactory>();
        ProviderRateLimitOptions limits =
            sp.GetRequiredService<IOptions<ProviderRateLimitOptions>>().Value;

        IMessageProvider chain = provider;

        chain = new ResilienceProviderDecorator(
            chain,
            limits.ResilienceFor(provider.Descriptor.Id),
            sp.GetRequiredService<TimeProvider>(),
            loggers.CreateLogger<ResilienceProviderDecorator>());

        chain = new RateLimitingProviderDecorator(
            chain,
            limits.LimiterFor(provider.Descriptor.Id),
            loggers.CreateLogger<RateLimitingProviderDecorator>());

        chain = new MetricsProviderDecorator(chain, sp.GetRequiredService<DeliveryMetrics>());

        chain = new LoggingProviderDecorator(
            chain,
            loggers.CreateLogger<LoggingProviderDecorator>());

        return chain;
    }
}
