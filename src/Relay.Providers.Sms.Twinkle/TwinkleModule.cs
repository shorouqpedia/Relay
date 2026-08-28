using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Relay.Domain.Messaging;
using Relay.Providers.Abstractions;

namespace Relay.Providers.Sms.Twinkle;

/// <summary>
/// Registers the Twinkle provider. Found by the host at startup; never referenced
/// by name from anywhere.
/// </summary>
public sealed class TwinkleModule : IProviderModule
{
    /// <inheritdoc />
    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId.Create("sms.twinkle").Value,
        ChannelType.Sms,
        ProviderCapabilities.ReturnsMessageId
        | ProviderCapabilities.PushesDeliveryReceipts
        | ProviderCapabilities.ReportsRetryAfter,
        TimeSpan.FromMinutes(15));

    /// <inheritdoc />
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<TwinkleOptions>()
            .Bind(configuration)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient<TwinkleProvider>((provider, client) =>
        {
            TwinkleOptions options = provider.GetRequiredService<IOptions<TwinkleOptions>>().Value;

            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);
            client.Timeout = options.Timeout;
        });

        // Keyed by provider id so the callback endpoint can resolve the right
        // verifier from a route segment without knowing which providers exist.
        services.AddKeyedScoped<ICallbackReceiver, TwinkleCallbackReceiver>(Descriptor.Id.Value);

        services.AddScoped<IMessageProvider>(sp => sp.GetRequiredService<TwinkleProvider>());
    }
}
