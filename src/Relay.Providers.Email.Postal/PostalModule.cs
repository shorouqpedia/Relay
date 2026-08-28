using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Refit;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Email.Postal;

/// <summary>
/// Registers the Postal provider. Found by the host at startup; never referenced
/// by name from anywhere.
/// </summary>
public sealed class PostalModule : IProviderModule
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId.Create("email.postal").Value,
        ChannelType.Email,
        ProviderCapabilities.ReturnsMessageId
        | ProviderCapabilities.PushesDeliveryReceipts
        | ProviderCapabilities.QueryableReceipts
        | ProviderCapabilities.NativeIdempotency
        | ProviderCapabilities.ReportsRetryAfter,
        TimeSpan.FromHours(6));

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PostalOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddRefitGeneratedClient, not AddRefitClient.
        //
        // Refit 15 builds the client at compile time with a source generator. The
        // non-generated overload falls back to a reflection-based builder that
        // ships in a separate package, and without it the failure is a
        // NotSupportedException at first resolve — so a provider registered that
        // way builds cleanly and dies on its first delivery.
        services
            .AddRefitGeneratedClient<IPostalApi>()
            .ConfigureHttpClient(static (provider, client) =>
            {
                PostalOptions options = provider.GetRequiredService<IOptions<PostalOptions>>().Value;

                client.BaseAddress = new Uri(options.BaseUrl);
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", options.ApiKey);

                // The client's own timeout, deliberately left in place rather than
                // relying only on the resilience decorator. This is the last line
                // that bounds how long a single physical call can hold a worker
                // slot, and it has to hold even if the decorator is misconfigured.
                client.Timeout = options.Timeout;
            });

        // Registered against the interface, not the concrete type. The host wraps
        // whatever is registered here in the decorator chain, and a consumer that
        // resolved PostalProvider directly would bypass every one of them.
        services.AddScoped<IMessageProvider, PostalProvider>();
    }
}
