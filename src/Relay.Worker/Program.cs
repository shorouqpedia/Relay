// Delivery pipeline host. Fleshed out in the pipeline milestone; for now this is
// a valid host so the project participates in the build and the CI gate.

using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
