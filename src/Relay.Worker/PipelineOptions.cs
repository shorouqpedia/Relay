using System.ComponentModel.DataAnnotations;

namespace Relay.Worker;

/// <summary>
/// How often each loop runs, and how much it takes at a time.
/// </summary>
/// <remarks>
/// The three cadences differ by orders of magnitude, and that is the point of
/// having three loops rather than one (ADR 0011). Dispatch runs constantly
/// because latency is visible to callers; reconciliation waits on windows measured
/// in hours; recovery is looking for something that only happens when a process
/// dies.
/// </remarks>
public sealed class PipelineOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "Pipeline";

    /// <summary>
    /// How many items each pass claims.
    /// </summary>
    /// <remarks>
    /// Bounded because a pass holds its row locks until it commits. A large batch
    /// on a slow provider keeps rows locked for a long time, which does not block
    /// other workers — <c>SKIP LOCKED</c> sees to that — but does mean a crash
    /// mid-batch leaves that many messages for the recovery loop to clean up.
    /// </remarks>
    [Range(1, 500)]
    public int BatchSize { get; init; } = 25;

    /// <summary>How long to wait after a dispatch pass that found work.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan DispatchInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest the dispatch loop waits when the queue keeps coming back empty.
    /// </summary>
    /// <remarks>
    /// This is the worst-case delay before an idle system notices a new message,
    /// so it is the number to lower if submission-to-send latency matters more
    /// than idle database load. Lowering it is also the cheap alternative to the
    /// <c>LISTEN</c>/<c>NOTIFY</c> option in ADR 0011.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan DispatchMaxInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often to look for messages whose receipt never arrived.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to look for dispatches abandoned by a dead worker.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a dispatch may run before it is treated as abandoned.
    /// </summary>
    /// <remarks>
    /// Must comfortably exceed the longest provider timeout plus everything the
    /// resilience chain adds on top of it. Set too low, this releases messages out
    /// from under workers that are still working on them — producing exactly the
    /// two-workers-one-message situation the row lock exists to prevent, from the
    /// other direction.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan StuckDispatchAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait after an outbox pass that published something.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan OutboxInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest the outbox loop waits when there is nothing to publish.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan OutboxMaxInterval { get; init; } = TimeSpan.FromSeconds(10);
}
