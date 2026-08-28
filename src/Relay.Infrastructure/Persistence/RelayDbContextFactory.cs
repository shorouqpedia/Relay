using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Relay.Infrastructure.Persistence;

/// <summary>
/// Builds a context for the EF Core tooling at design time.
/// </summary>
/// <remarks>
/// Used only by <c>dotnet ef</c> when adding a migration or producing a script.
/// The alternative is to point the tooling at a host project, which makes
/// generating a migration depend on the host's whole startup path being
/// constructible — configuration sources, provider discovery, options validation
/// — none of which has anything to do with the model.
/// <para>
/// The connection string here is never used to connect. Migrations are generated
/// from the model, and the provider only needs to know which SQL dialect to
/// write. A real connection string in this file would be a credential in source
/// control that nothing depends on.
/// </para>
/// </remarks>
internal sealed class RelayDbContextFactory : IDesignTimeDbContextFactory<RelayDbContext>
{
    public RelayDbContext CreateDbContext(string[] args)
    {
        DbContextOptions<RelayDbContext> options =
            new DbContextOptionsBuilder<RelayDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=relay_design_time",

                    // Must match the runtime registration. The history table name
                    // is not part of the model, so nothing detects a mismatch: a
                    // script generated here would write to one table while the
                    // application reads another, and the application would then
                    // conclude that every migration is still pending and try to
                    // create tables that already exist.
                    npgsql => npgsql.MigrationsHistoryTable(
                        PersistenceServiceCollectionExtensions.MigrationsHistoryTable))
                .Options;

        return new RelayDbContext(options);
    }
}
