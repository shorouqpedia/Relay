using Npgsql;
using Relay.Infrastructure.Persistence;
using Respawn;
using Respawn.Graph;

namespace Relay.IntegrationTests.Persistence;

/// <summary>
/// Returns a test database to empty between tests.
/// </summary>
/// <remarks>
/// The containers are shared across a collection because starting one dominates
/// the runtime; without this, the tables were shared too. Every test then ran
/// against whatever the tests before it had left behind, and whether that
/// mattered depended on the order they happened to run in. It first showed as
/// PR08 claiming a row another test had seeded — passing on one machine, failing
/// on the next — and a test whose outcome depends on its neighbours is not
/// testing what its name says.
/// <para>
/// Respawn deletes rows rather than dropping and re-migrating, and works out the
/// order from the foreign keys itself, so a reset costs milliseconds and a new
/// table needs no edit here. The migrations history is kept: emptying it would
/// make the next <c>MigrateAsync</c> try to create tables that already exist.
/// </para>
/// </remarks>
internal sealed class DatabaseReset
{
    private readonly string _connectionString;
    private Respawner? _respawner;

    public DatabaseReset(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        // Built on first use rather than in the fixture's InitializeAsync, because
        // Respawn reads the schema to plan the deletes and the schema exists only
        // after the migrations have run.
        _respawner ??= await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Table(PersistenceServiceCollectionExtensions.MigrationsHistoryTable)],
        });

        await _respawner.ResetAsync(connection);
    }
}
