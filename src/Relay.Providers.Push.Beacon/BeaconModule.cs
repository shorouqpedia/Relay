using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Push.Beacon;

/// <summary>
/// Registers the Beacon provider. Found by the host at startup; never referenced
/// by name from anywhere.
/// </summary>
public sealed class BeaconModule : IProviderModule
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId.Create("push.beacon").Value,
        ChannelType.Push,
        ProviderCapabilities.ReturnsMessageId,
        TimeSpan.FromMinutes(5));

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<BeaconOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<BeaconProvider>((provider, client) =>
        {
            BeaconOptions options = provider.GetRequiredService<IOptions<BeaconOptions>>().Value;

            // The trailing slash is load-bearing.
            //
            // HttpClient resolves a relative request URI against BaseAddress by URI
            // rules, which replace the last path segment. So a base of
            // "https://host/vendor" with a request of "send" produces
            // "https://host/send" — the vendor prefix silently disappears, and the
            // symptom is a 404 that looks like the endpoint moved.
            //
            // Normalised here rather than trusted to configuration, because a
            // missing slash in a settings file is not something review catches.
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
            client.Timeout = options.Timeout;
        });

        services.AddScoped<IMessageProvider>(sp => sp.GetRequiredService<BeaconProvider>());
    }
}
