using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Api;
using Relay.Infrastructure.Persistence;
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

        // The provider needs a syntactically valid configuration to pass its
        // startup validation. Nothing in these tests calls out — the delivery
        // loops live in the worker, not the API — so the address is never dialled.
        builder.UseSetting("Providers:email.postal:BaseUrl", "https://postal.invalid");
        builder.UseSetting("Providers:email.postal:ApiKey", "integration-test-key");
        builder.UseSetting("Providers:email.postal:FromAddress", "relay@example.com");
    }
}

/// <summary>Binds the API fixture to the tests that share it.</summary>
[CollectionDefinition(Name)]
public sealed class RelayApiCollection : ICollectionFixture<RelayApiFactory>
{
    /// <summary>The collection name.</summary>
    public const string Name = "relay-api";
}
