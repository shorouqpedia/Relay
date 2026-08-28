using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Relay.Application.Delivery;
using Relay.Domain.Messaging;

namespace Relay.Worker;

/// <summary>
/// Claims pending messages and delivers them.
/// </summary>
/// <remarks>
/// The hot loop. Claims a batch under <c>FOR UPDATE SKIP LOCKED</c> and dispatches
/// each message in turn, so several instances of the worker can share one table
/// without either blocking on each other or sending anything twice.
/// <para>
/// Messages within a batch are dispatched sequentially, not in parallel. Parallel
/// dispatch would multiply throughput and would also multiply the rate at which a
/// struggling provider is hit, right when the rate limiter is trying to reduce it.
/// Concurrency here comes from running more workers, which is the axis the
/// row-level claim already makes safe.
/// </para>
/// </remarks>
internal sealed class DispatchLoop(
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    TimeProvider clock,
    ILogger<DispatchLoop> logger) : PollingService(scopes, clock, logger)
{
    private readonly PipelineOptions _options = options.Value;

    protected override string LoopName => "Dispatch loop";

    protected override TimeSpan BaseInterval => _options.DispatchInterval;

    protected override TimeSpan MaxInterval => _options.DispatchMaxInterval;

    protected override async Task<int> RunPassAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        IMessageRepository messages = services.GetRequiredService<IMessageRepository>();
        MessageDispatcher dispatcher = services.GetRequiredService<MessageDispatcher>();

        IReadOnlyList<Message> claimed = await messages
            .ClaimPendingAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);

        foreach (Message message in claimed)
        {
            await dispatcher.DispatchAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return claimed.Count;
    }
}

/// <summary>
/// Resolves messages whose delivery receipt never arrived.
/// </summary>
/// <remarks>
/// Runs on a much slower cadence than dispatch, because it is waiting on windows
/// measured in minutes and hours. Polling it at dispatch speed would query a
/// nearly always empty index thousands of times to find something that could not
/// possibly have changed.
/// </remarks>
internal sealed class ReconciliationLoop(
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    TimeProvider clock,
    ILogger<ReconciliationLoop> logger) : PollingService(scopes, clock, logger)
{
    private readonly PipelineOptions _options = options.Value;

    protected override string LoopName => "Reconciliation loop";

    protected override TimeSpan BaseInterval => _options.ReconciliationInterval;

    protected override TimeSpan MaxInterval => _options.ReconciliationInterval;

    protected override async Task<int> RunPassAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        await services.GetRequiredService<ReceiptReconciler>()
            .ReconcileAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// Returns messages abandoned mid-dispatch to the queue.
/// </summary>
internal sealed class RecoveryLoop(
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    TimeProvider clock,
    ILogger<RecoveryLoop> logger) : PollingService(scopes, clock, logger)
{
    private readonly PipelineOptions _options = options.Value;

    protected override string LoopName => "Recovery loop";

    protected override TimeSpan BaseInterval => _options.RecoveryInterval;

    protected override TimeSpan MaxInterval => _options.RecoveryInterval;

    protected override async Task<int> RunPassAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        await services.GetRequiredService<StuckDispatchRecovery>()
            .RecoverAsync(_options.StuckDispatchAfter, _options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>
/// Publishes what the outbox has accumulated.
/// </summary>
/// <remarks>
/// Separate from the three delivery loops because it serves a different purpose:
/// the delivery loops keep Relay correct, and this one keeps everyone else
/// informed. Relay's own pipeline does not depend on it running (ADR 0011), so it
/// falling behind delays notifications rather than stopping deliveries.
/// </remarks>
internal sealed class OutboxLoop(
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    TimeProvider clock,
    ILogger<OutboxLoop> logger) : PollingService(scopes, clock, logger)
{
    private readonly PipelineOptions _options = options.Value;

    protected override string LoopName => "Outbox loop";

    protected override TimeSpan BaseInterval => _options.OutboxInterval;

    protected override TimeSpan MaxInterval => _options.OutboxMaxInterval;

    protected override async Task<int> RunPassAsync(
        IServiceProvider services,
        CancellationToken cancellationToken) =>
        await services
            .GetRequiredService<Infrastructure.Persistence.Outbox.OutboxDispatcher>()
            .DispatchAsync(_options.BatchSize, cancellationToken)
            .ConfigureAwait(false);
}
