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
            client.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
            client.Timeout = options.Timeout;
        });

        // Keyed by provider id, so the callback endpoint can resolve the right
        // verifier from a route segment without the host knowing which providers
        // exist.
        services.AddKeyedScoped<ICallbackReceiver, MailhookCallbackReceiver>(Descriptor.Id.Value);

        // Resolved through the typed-client registration above, then handed back
        // as IMessageProvider so the host's decorator chain wraps it. Registering
        // the concrete type against the interface directly would bypass
        // AddHttpClient's factory and give the provider an unconfigured client.
        services.AddScoped<IMessageProvider>(sp => sp.GetRequiredService<MailhookProvider>());
    }
}
