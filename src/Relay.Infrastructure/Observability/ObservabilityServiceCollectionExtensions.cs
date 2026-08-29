using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Relay.Application.Observability;
using Relay.Infrastructure.Delivery.Decorators;

namespace Relay.Infrastructure.Observability;

/// <summary>
/// Wires OpenTelemetry for a Relay host.
/// </summary>
/// <remarks>
/// Shared by the API and the worker so both emit the same resource attributes
/// and the same instrument names. Two hosts describing themselves differently is
/// how a dashboard ends up needing a query per process.
/// </remarks>
public static class ObservabilityServiceCollectionExtensions
{
    /// <summary>Adds tracing and metrics.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configuration">Read for the exporter endpoint.</param>
    /// <param name="serviceName">Which host this is, as it will appear in traces.</param>
    public static IServiceCollection AddRelayObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        string serviceName)
    {
        string? exporterEndpoint = configuration["OpenTelemetry:Endpoint"];

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName,
                    serviceVersion: typeof(ObservabilityServiceCollectionExtensions)
                        .Assembly.GetName().Version?.ToString(),

                    // A stable identity per process, so a trace can be attributed
                    // to one instance among several. Regenerated on restart, which
                    // is what makes "did this only happen on one pod" answerable.
                    serviceInstanceId: Environment.MachineName)
                .AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(RelayTelemetry.SourceName)
                    .AddHttpClientInstrumentation()
                    .AddAspNetCoreInstrumentation(options =>

                        // Health checks are polled every few seconds by whatever is
                        // watching the process. Tracing them would bury real traffic
                        // under noise that says nothing, and cost sampling budget
                        // that should go to deliveries.
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health"));

                Export(tracing, exporterEndpoint);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(DeliveryMetrics.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()

                    // The runtime meter, for the questions that turn out to be the
                    // answer surprisingly often: thread-pool starvation under load,
                    // and gen-2 collections during a backlog drain.
                    .AddRuntimeInstrumentation();

                Export(metrics, exporterEndpoint);
            });

        return services;
    }

    /// <summary>
    /// Sends traces to a collector, or nowhere.
    /// </summary>
    /// <remarks>
    /// No endpoint means no exporter, and that is a supported configuration rather
    /// than a broken one. A developer running the API without a collector should
    /// get a working service, not a process retrying a connection to localhost
    /// every few seconds and logging the failure.
    /// </remarks>
    private static void Export(TracerProviderBuilder tracing, string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            tracing.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
        }
    }

    private static void Export(MeterProviderBuilder metrics, string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            metrics.AddOtlpExporter(options => options.Endpoint = new Uri(endpoint));
        }
    }
}
