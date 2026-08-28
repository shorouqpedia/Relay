// The delivery pipeline host.
//
// Deliberately a separate process from the API. The two scale on different
// signals — request traffic and queue depth — and a slow provider must not
// degrade request latency (ADR 0002).

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;
using Relay.Infrastructure.Persistence.Outbox;
using Relay.Worker;
using Serilog;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog((services, configuration) => configuration
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddRelayPersistence(builder.Configuration);
builder.Services.AddRelayDelivery(builder.Configuration);

builder.Services
    .AddOptions<PipelineOptions>()
    .Bind(builder.Configuration.GetSection(PipelineOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// A no-op publisher until the broker arrives. Registered explicitly rather than
// left missing, so the outbox loop runs end to end — rows are claimed, marked
// processed, and the claim-and-mark path is exercised — instead of failing at
// resolve and hiding whether any of it works.
builder.Services.AddSingleton<IOutboxPublisher, LoggingOutboxPublisher>();

builder.Services.AddHostedService<DispatchLoop>();
builder.Services.AddHostedService<ReconciliationLoop>();
builder.Services.AddHostedService<RecoveryLoop>();
builder.Services.AddHostedService<OutboxLoop>();

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
