using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Relay.Domain.Common;
using Relay.Application.Messages;
using Relay.Domain.Messaging;
using Relay.Infrastructure.Persistence.Outbox;
using Relay.Infrastructure.Persistence.Repositories;

namespace Relay.Infrastructure.Persistence;

/// <summary>
/// Registers persistence. One entry point per infrastructure module, so a host's
/// startup file is a list of module registrations rather than a hundred lines of
/// container plumbing.
/// </summary>
public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Where EF Core records which migrations have been applied.
    /// </summary>
    /// <remarks>
    /// A constant because the design-time factory has to use the same value, and
    /// nothing would detect a mismatch: the history table is not part of the
    /// model, so a script written against one name and an application reading
    /// another would simply disagree about what has been applied.
    /// </remarks>
    public const string MigrationsHistoryTable = "__migrations";

    /// <summary>Adds the database, the repositories, and the outbox.</summary>
    public static IServiceCollection AddRelayPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("Relay");

        // Empty, not just null. appsettings.json ships the key with an empty value
        // so that the shape of the configuration is visible and CI can assert no
        // secret was committed — which means the absent case in practice is "" and
        // not null. Checking only for null let that through, and the failure
        // surfaced far away as "the ConnectionString property has not been
        // initialized" from inside the driver.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Connection string 'Relay' is not configured. The process cannot "
                + "usefully start without a database, so this fails at startup "
                + "rather than on the first request.");
        }

        services.AddDbContext<RelayDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsHistoryTable(MigrationsHistoryTable);

                // Retries on transient connection faults. Note that this is
                // EF-level and applies to the whole SaveChanges: a retry re-runs
                // the command, so anything not idempotent at the database level
                // must be protected by a constraint rather than by a check —
                // which is the argument for the unique index in ADR 0008 arriving
                // from a second direction.
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            });

            // Queries are tracked by default because every repository method here
            // loads an aggregate that is about to change. Read models project
            // straight to DTOs and opt out explicitly at the call site.
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll);
        });

        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IMessageReader, MessageReader>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<OutboxDispatcher>();

        // TimeProvider rather than DateTimeOffset.UtcNow, so that "the receipt
        // window elapsed" is a testable condition rather than a wall-clock wait.
        services.TryAddSingletonTimeProvider();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TimeProvider)))
        {
            return;
        }

        services.AddSingleton(TimeProvider.System);
    }
}
