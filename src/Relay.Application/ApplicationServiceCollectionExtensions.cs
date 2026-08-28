using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Relay.Application.Messages;

namespace Relay.Application;

/// <summary>
/// Registers the use cases and their validators.
/// </summary>
/// <remarks>
/// Handlers are registered as themselves rather than behind an interface. There
/// is exactly one implementation of each and exactly one caller, so an interface
/// would add a name to maintain and nothing else — see ADR 0012 for the same
/// argument applied to a mediator.
/// </remarks>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>Adds the message use cases.</summary>
    public static IServiceCollection AddRelayApplication(this IServiceCollection services)
    {
        services.AddScoped<SubmitMessageHandler>();
        services.AddScoped<CancelMessageHandler>();

        // Scanned rather than listed. A validator that exists but was never
        // registered is worse than one that does not exist: the endpoint accepts
        // input nobody checked, and nothing fails.
        services.AddValidatorsFromAssemblyContaining<SubmitMessageValidator>(
            ServiceLifetime.Singleton);

        return services;
    }
}
