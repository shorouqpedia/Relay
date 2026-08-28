using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Api;
using Relay.Infrastructure.Persistence;
using Relay.Providers.Abstractions;
using Relay.IntegrationTests.Persistence;
using Testcontainers.PostgreSql;

namespace Relay.IntegrationTests.Api;

/// <summary>
/// The real API host, in-process, against a real database.
/// </summary>
/// <remarks>
/// The host is the one from <c>Program.cs</c> — the same registrations, the same
/// middleware, the same provider discovery. Only the connection string is
/// replaced, and only because the container's port is not known until it starts.
/// <para>
/// Nothing else is substituted. Swapping the repository for a fake here would
/// leave the suite testing the endpoints against a system that does not exist,
/// and the failures worth catching at this level — a projection that does not
/// translate, a unique index doing its job, a filter that never runs — are
/// exactly the ones a substitution hides.
/// </para>
/// </remarks>
public sealed class RelayApiFactory : WebApplicationFactory<RelayApi>, IAsyncLifetime
{
    /// <summary>
    /// The Postal callback secret this host runs with.
    /// </summary>
    /// <remarks>
    /// Shared with the callback tests so they can produce signatures the host
    /// will accept. There is no way around knowing it: the tests exist to prove
    /// that a correct signature is honoured and an incorrect one is not, and
    /// neither half can be written without the secret.
    /// </remarks>
    public const string PostalCallbackSecret = "integration-test-callback-secret-32ch";

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("relay_api")
        .WithUsername("relay")
        .WithPassword("relay")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        await using AsyncServiceScope scope = Services.CreateAsyncScope();
        RelayDbContext context = scope.ServiceProvider.GetRequiredService<RelayDbContext>();

        // Migrated, not EnsureCreated. The suite should fail if a migration does
        // not apply, which is a thing EnsureCreated cannot tell you because it
        // builds the schema from the model and skips migrations entirely.
        await context.Database.MigrateAsync();
    }

    /// <summary>
    /// Opens a context against the same database the host is using.
    /// </summary>
    /// <remarks>
    /// A fresh one per call, resolved from a new scope. Reading through a context
    /// the host has already used would show the test whatever is in that change
    /// tracker rather than what was committed — so an assertion could pass on a
    /// value that never reached the database.
    /// </remarks>
    public RelayDbContext NewDbContext()
    {
        IServiceScope scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RelayDbContext>();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration.
        //
        // A host built with WebApplicationBuilder reads its configuration while
        // Program.cs is still running — before ConfigureAppConfiguration callbacks
        // are applied — so overrides added there arrive too late to be seen by
        // registration code. They are silently ignored rather than rejected, so
        // the symptom is not "your override was skipped" but an empty connection
        // string much deeper in. UseSetting goes into host configuration, which is
        // read early enough.
        builder.UseSetting("ConnectionStrings:Relay", _database.GetConnectionString());

        // Every discovered provider is configured, not just the ones a test uses.
        //
        // Options are validated at startup, so an unconfigured provider stops the
        // host from starting at all — which is correct behaviour and would
        // otherwise mean adding a provider breaks this file. Generating the
        // settings from what the host will discover keeps that from happening.
        //
        // Nothing here is ever dialled: the delivery loops live in the worker, not
        // the API. These values exist to satisfy validation, and the addresses
        // point at .invalid precisely so a mistake fails to resolve rather than
        // reaching something real.
        foreach (string id in ProviderIds())
        {
            builder.UseSetting($"Providers:{id}:BaseUrl", $"https://{id}.invalid");
            builder.UseSetting($"Providers:{id}:ApiKey", "integration-test-key");
            builder.UseSetting($"Providers:{id}:FromAddress", "relay@example.com");
            builder.UseSetting($"Providers:{id}:AccountId", "integration-test-account");
            builder.UseSetting($"Providers:{id}:SenderId", "Relay");
            builder.UseSetting($"Providers:{id}:DefaultTitle", "Relay");
            builder.UseSetting($"Providers:{id}:SigningSecret", "integration-test-signing-secret-32ch");
            builder.UseSetting($"Providers:{id}:CallbackSecret", PostalCallbackSecret);
        }
    }

    /// <summary>
    /// The provider ids the host will discover.
    /// </summary>
    /// <remarks>
    /// Found the same way the host finds them — by scanning the deployment
    /// directory — so the set configured here is by construction the set that will
    /// be registered.
    /// </remarks>
    private static IEnumerable<string> ProviderIds()
    {
        foreach (string path in Directory.EnumerateFiles(
                     AppContext.BaseDirectory,
                     "Relay.Providers.*.dll"))
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(path);
            }
            catch (Exception exception) when (exception is BadImageFormatException or FileLoadException)
            {
                continue;
            }

            foreach (Type type in assembly.GetTypes())
            {
                if (typeof(IProviderModule).IsAssignableFrom(type)
                    && type is { IsAbstract: false, IsInterface: false }
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    yield return ((IProviderModule)Activator.CreateInstance(type)!).Descriptor.Id.Value;
                }
            }
        }
    }
}

/// <summary>Binds the API fixture to the tests that share it.</summary>
[CollectionDefinition(Name)]
public sealed class RelayApiCollection : ICollectionFixture<RelayApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "relay-api";
}
