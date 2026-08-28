using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Email.Mailhook;

/// <summary>
/// Registers the Mailhook provider. Found by the host at startup; never referenced
/// by name from anywhere.
/// </summary>
public sealed class MailhookModule : IProviderModule
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId.Create("email.mailhook").Value,
        ChannelType.Email,
        ProviderCapabilities.ReturnsMessageId
        | ProviderCapabilities.PushesDeliveryReceipts
        | ProviderCapabilities.QueryableReceipts
        | ProviderCapabilities.ReportsRetryAfter,
        TimeSpan.FromHours(4));

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<MailhookOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<MailhookProvider>((provider, client) =>
        {
            MailhookOptions options = provider.GetRequiredService<IOptions<MailhookOptions>>().Value;

            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = options.Timeout;
        });

        // Resolved through the typed-client registration above, then handed back
        // as IMessageProvider so the host's decorator chain wraps it. Registering
        // the concrete type against the interface directly would bypass
        // AddHttpClient's factory and give the provider an unconfigured client.
        services.AddScoped<IMessageProvider>(sp => sp.GetRequiredService<MailhookProvider>());
    }
}
