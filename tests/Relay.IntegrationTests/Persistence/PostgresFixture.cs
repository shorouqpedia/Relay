using Microsoft.EntityFrameworkCore;
using Relay.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Relay.IntegrationTests.Persistence;

/// <summary>
/// A real PostgreSQL, started for the test run and thrown away afterwards.
/// </summary>
/// <remarks>
/// The reason this is a container and not the EF in-memory provider is that
/// almost everything these tests assert is a property of the storage engine
/// rather than of the model: a unique index rejecting a concurrent duplicate,
/// <c>xmin</c> changing on write, <c>FOR UPDATE SKIP LOCKED</c> handing two
/// workers disjoint rows. The in-memory provider enforces none of them, so a
/// suite passing against it would prove nothing about the guarantees the design
/// rests on (ADR 0010).
/// <para>
/// Shared across the collection rather than created per test, because container
/// startup dominates the runtime. Tests share the database but not its contents:
/// each one starts by emptying the tables (<see cref="DatabaseReset"/>).
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("relay")
        .WithUsername("relay")
        .WithPassword("relay")
        .Build();

    /// <summary>Connection string for the running container.</summary>
    public string ConnectionString => _container.GetConnectionString();

    private DatabaseReset? _reset;

    /// <summary>Empties every table. Called by each test before it runs.</summary>
    public Task ResetAsync() => (_reset ??= new DatabaseReset(ConnectionString)).ResetAsync();

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();

        // The schema is created by running the migrations, not by EnsureCreated.
        //
        // EnsureCreated builds the schema from the model and skips migrations
        // entirely, which means the tests would verify a schema no deployment ever
        // produces — and a migration that fails to apply would still leave the
        // suite green. Running them here makes the migrations themselves part of
        // what is under test.
        await using RelayDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Opens a new context against the container.</summary>
    /// <remarks>
    /// A fresh context per call, deliberately. Several tests need two independent
    /// units of work to represent two workers, and sharing one change tracker
    /// between them would make the first writer's state visible to the second —
    /// hiding exactly the conflict being tested.
    /// </remarks>
    public RelayDbContext CreateContext()
    {
        DbContextOptions<RelayDbContext> options =
            new DbContextOptionsBuilder<RelayDbContext>()
                .UseNpgsql(
                    ConnectionString,
                    npgsql => npgsql.MigrationsHistoryTable(
                        PersistenceServiceCollectionExtensions.MigrationsHistoryTable))
                .Options;

        return new RelayDbContext(options);
    }
}

/// <summary>Binds the fixture to the tests that share it.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "postgres";
}
