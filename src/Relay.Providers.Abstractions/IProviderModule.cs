using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Relay.Providers.Abstractions;

/// <summary>
/// A provider assembly's entry point. Implement it once per assembly, and the
/// host finds it.
/// </summary>
/// <remarks>
/// This is the mechanism behind the zero-edit claim in ADR 0003. Registration is
/// inverted: the host does not know which providers exist, so adding one does not
/// change the host. A composition root with a list of providers in it would put
/// every provider's existence into shared code, and adding the sixth would mean
/// editing the file that the first five already share.
/// <para>
/// Implementations are found by scanning loaded assemblies for public,
/// parameterless-constructible types implementing this interface. Discovery is by
/// convention, which is the cost: a module with a non-public constructor, or one
/// in an assembly the host never loads, is silently absent. The host logs the set
/// it discovered at startup for exactly that reason, and a startup check fails
/// the process when a provider named in routing configuration was not found.
/// </para>
/// </remarks>
public interface IProviderModule
{
    /// <summary>
    /// The provider this module registers.
    /// </summary>
    /// <remarks>
    /// Available before <see cref="Register"/> runs, so the host can log what it
    /// found and reject a duplicate identifier without building anything.
    /// </remarks>
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Registers the provider and whatever it needs.
    /// </summary>
    /// <remarks>
    /// A module registers its own <see cref="IMessageProvider"/> implementation,
    /// its typed HTTP client, and its options. It does not register cross-cutting
    /// concerns: retries, rate limiting, metrics, and logging are applied by the
    /// host as decorators (ADR 0007), identically for every provider. A module
    /// that adds its own retry policy is fighting the chain rather than using it.
    /// <para>
    /// Configuration is scoped to <c>Providers:{Id}</c> by the host before this is
    /// called, so a module reads its own settings from the root of what it is
    /// given and cannot read another provider's.
    /// </para>
    /// </remarks>
    /// <param name="services">The container being built.</param>
    /// <param name="configuration">This provider's configuration section.</param>
    void Register(IServiceCollection services, IConfiguration configuration);
}
