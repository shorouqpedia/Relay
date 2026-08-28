using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Webhook;

/// <summary>
/// Registers the webhook provider. Found by the host at startup; never referenced
/// by name from anywhere.
/// </summary>
public sealed class WebhookModule : IProviderModule
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId.Create("webhook").Value,
        ChannelType.Webhook,
        ProviderCapabilities.None,
        TimeSpan.FromMinutes(1));

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<WebhookOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<WebhookProvider>((provider, client) =>
        {
            WebhookOptions options = provider.GetRequiredService<IOptions<WebhookOptions>>().Value;

            // No BaseAddress. Every message goes to a different host — the one the
            // recipient supplied — so there is nothing to configure a base for, and
            // setting one would silently rewrite the destination.
            client.Timeout = options.Timeout;
        });

        services.AddScoped<IMessageProvider>(sp => sp.GetRequiredService<WebhookProvider>());
    }
}
