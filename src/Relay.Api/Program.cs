using Asp.Versioning;
using Asp.Versioning.Builder;
using Relay.Api;
using Relay.Application;
using Relay.Domain.Common;
using Relay.Infrastructure.Delivery;
using Relay.Infrastructure.Persistence;
using Scalar.AspNetCore;
using Serilog;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Serilog reads its own configuration, so changing a log level or adding a sink
// is a config change rather than a deployment.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// One line per module. The composition root's job is to say which modules exist,
// not to know what is inside them — each AddRelay* method owns its own
// registrations (see the DependencyInjection files in each assembly).
builder.Services.AddRelayApplication();
builder.Services.AddRelayPersistence(builder.Configuration);
builder.Services.AddRelayDelivery(builder.Configuration);

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;

    // The version comes from the URL segment and nowhere else. Left unset, the
    // library also accepts a query string and a header, so one endpoint has three
    // addresses — which makes caching, logs, and access rules all disagree about
    // what was called.
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Enums cross the wire as names, not as the numbers System.Text.Json uses by
    // default. Numbers make the API depend on declaration order — inserting a
    // member reassigns every value after it and silently changes what existing
    // clients mean — and they make a request body unreadable in a log.
    //
    // Without this, `"channel": "Email"` fails to bind and the caller gets a bare
    // 400 with no indication of which field was wrong.
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddOpenApi();

// The problem-details service supplies the RFC 7807 shape for framework-produced
// responses — a 404 from routing, a 415 from content negotiation — so those match
// what ErrorResults produces for domain failures. Without it a client would meet
// two different error shapes depending on how far into the request it got.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RelayDbContext>("database");


WebApplication app = builder.Build();

// The one place unhandled exceptions are turned into responses. Everything that
// reaches here is unexpected by definition — expected failures are Result values
// (ADR 0005), which is what lets handlers avoid catching anything (ADR 0009).
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseSerilogRequestLogging();

ApiVersionSet versions = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1, 0))
    .ReportApiVersions()
    .Build();

app.MapGroup(string.Empty)
    .WithApiVersionSet(versions)
    .MapMessages();

app.MapGroup(string.Empty)
    .WithApiVersionSet(versions)
    .MapCallbacks();

app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

await app.RunAsync().ConfigureAwait(false);
