using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Relay.Worker;

/// <summary>
/// A loop that polls for work, does it, and backs off when there is none.
/// </summary>
/// <remarks>
/// Shared by all three pipeline loops (ADR 0011). They differ in what they claim
/// and how often, not in how they behave around it: every one of them has to
/// resolve a scope per pass, survive a failing pass, and avoid polling an empty
/// table at full speed.
/// <para>
/// Backoff is on emptiness, not on failure. A loop that finds nothing doubles its
/// wait up to a ceiling; a loop that finds work resets to the base interval, so a
/// burst is drained at full speed and a quiet night costs one query per ceiling
/// interval instead of one per second.
/// </para>
/// </remarks>
internal abstract class PollingService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger logger) : BackgroundService
{
    /// <summary>Name used in logs.</summary>
    protected abstract string LoopName { get; }

    /// <summary>How long to wait after a pass that found work.</summary>
    protected abstract TimeSpan BaseInterval { get; }

    /// <summary>The longest the loop will wait after repeatedly finding nothing.</summary>
    protected abstract TimeSpan MaxInterval { get; }

    /// <summary>
    /// Does one pass.
    /// </summary>
    /// <returns>How many items were handled. Zero triggers the backoff.</returns>
    protected abstract Task<int> RunPassAsync(
        IServiceProvider services,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Loop} started.", LoopName);

        TimeSpan delay = BaseInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            int handled = await RunOnePassAsync(stoppingToken).ConfigureAwait(false);

            delay = handled > 0
                ? BaseInterval
                : Min(Double(delay), MaxInterval);

            try
            {
                await Task.Delay(delay, clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("{Loop} stopped.", LoopName);
    }

    private async Task<int> RunOnePassAsync(CancellationToken stoppingToken)
    {
        // A scope per pass, not per loop. The unit of work and the DbContext are
        // scoped, and a long-lived one would accumulate every message the loop has
        // ever touched in its change tracker — which leaks memory and, worse, means
        // a stale entity from an hour ago is still tracked and can be saved.
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        try
        {
            return await RunPassAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The loop must outlive a bad pass. An unhandled exception here would
            // stop the BackgroundService permanently and silently — the process
            // stays up, healthy by every external measure, and delivers nothing.
            //
            // This is not the catch-all ADR 0009 rules out: nothing is converted
            // into a friendly result, the failure is logged in full, and the only
            // behaviour is to try again next tick.
            logger.LogError(exception, "{Loop} pass failed. The loop continues.", LoopName);

            return 0;
        }
    }

    private static TimeSpan Double(TimeSpan value) => value * 2;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
