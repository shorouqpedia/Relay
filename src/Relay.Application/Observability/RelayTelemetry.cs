using System.Diagnostics;

namespace Relay.Application.Observability;

/// <summary>
/// The names Relay's own spans and attributes use.
/// </summary>
/// <remarks>
/// Constants rather than string literals at each call site, because attribute
/// names are a contract with whatever queries them. A dashboard filtering on
/// <c>relay.message.id</c> silently returns nothing when one span spells it
/// <c>message_id</c>, and nothing fails — the query just stops matching.
/// <para>
/// Names follow the OpenTelemetry convention: lowercase, dot-separated,
/// namespaced by the system that owns them.
/// </para>
/// </remarks>
public static class RelayTelemetry
{
    /// <summary>The activity source name, registered with OpenTelemetry by both hosts.</summary>
    public const string SourceName = "Relay";

    /// <summary>
    /// The source every Relay span comes from.
    /// </summary>
    /// <remarks>
    /// Static and never disposed. An <see cref="ActivitySource"/> is inert unless
    /// something is listening, so this costs nothing when tracing is off — which
    /// is what makes it safe to instrument the hot path.
    /// </remarks>
    public static readonly ActivitySource Source = new(SourceName);

    /// <summary>Attribute names shared across spans.</summary>
    public static class Tags
    {
        /// <summary>Relay's identifier for the message.</summary>
        public const string MessageId = "relay.message.id";

        /// <summary>The channel it is being delivered on.</summary>
        public const string Channel = "relay.message.channel";

        /// <summary>Where the message is in its lifecycle.</summary>
        public const string Status = "relay.message.status";

        /// <summary>Which provider was chosen.</summary>
        public const string Provider = "relay.provider.id";

        /// <summary>How the attempt ended.</summary>
        public const string Outcome = "relay.delivery.outcome";

        /// <summary>Which attempt this is, starting at one.</summary>
        public const string Attempt = "relay.delivery.attempt";

        /// <summary>The provider's own identifier for the message.</summary>
        public const string ProviderMessageId = "relay.provider.message_id";

        /// <summary>How many items a batch operation handled.</summary>
        public const string BatchSize = "relay.batch.size";

        /// <summary>What was decided about an inbound callback.</summary>
        public const string CallbackDecision = "relay.callback.decision";
    }

    /// <summary>Span names.</summary>
    public static class Spans
    {
        /// <summary>Accepting a message over HTTP.</summary>
        public const string Submit = "relay.submit";

        /// <summary>One logical delivery attempt, including retries inside it.</summary>
        public const string Dispatch = "relay.dispatch";

        /// <summary>One pass of a polling loop.</summary>
        public const string Poll = "relay.poll";

        /// <summary>Handling one inbound callback.</summary>
        public const string Callback = "relay.callback";

        /// <summary>Reconciling messages whose receipts never arrived.</summary>
        public const string Reconcile = "relay.reconcile";
    }

    /// <summary>
    /// Starts a span linked to the trace a message was submitted on.
    /// </summary>
    /// <remarks>
    /// A link, not a parent (ADR 0014). The submission request finished long
    /// before this runs, so making its span the parent would produce a trace with
    /// no end and a duration measured in hours — which breaks latency percentiles
    /// and, on most backends, the rendering too.
    /// <para>
    /// A message with no recorded context, or an unparseable one, produces a span
    /// with no link rather than no span. An unlinked trace is still a useful trace,
    /// and refusing to trace at all because the correlation is missing would be a
    /// strange way to improve observability.
    /// </para>
    /// </remarks>
    /// <param name="name">The span name.</param>
    /// <param name="submissionTraceParent">The W3C <c>traceparent</c> recorded at submission.</param>
    /// <param name="kind">The span kind.</param>
    public static Activity? StartLinked(
        string name,
        string? submissionTraceParent,
        ActivityKind kind = ActivityKind.Internal)
    {
        ActivityContext.TryParse(submissionTraceParent, null, out ActivityContext submission);

        ActivityLink[] links = submission == default ? [] : [new ActivityLink(submission)];

        // Activity.Current is cleared for the duration of the start call.
        //
        // Passing `parentContext: default` does not mean "no parent" — .NET reads
        // it as "no parent was specified" and falls back to the ambient activity.
        // So a dispatch running inside any other span would silently continue that
        // span's trace, which is the exact thing this method exists to prevent.
        //
        // Today the worker has nothing ambient and the bug would not show. It
        // would appear the first time dispatch ran inside a request — an admin
        // endpoint that flushes the queue, or an in-process test harness — and the
        // symptom would be traces that never end, in one hosting shape and not the
        // other. The unit test for this failed on the first run.
        Activity? ambient = Activity.Current;
        Activity.Current = null;

        try
        {
            return Source.StartActivity(name, kind, parentContext: default, links: links);
        }
        finally
        {
            Activity.Current = ambient;
        }
    }
}
