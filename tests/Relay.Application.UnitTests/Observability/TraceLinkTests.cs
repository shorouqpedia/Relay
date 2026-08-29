using System.Diagnostics;
using Relay.Application.Observability;

namespace Relay.Application.UnitTests.Observability;

/// <summary>
/// How a dispatch span relates to the submission that caused it.
/// </summary>
/// <remarks>
/// The distinction these assert is invisible in normal use and expensive to get
/// wrong: a parent-child relationship across a queue produces traces that never
/// end and durations measured in hours, which quietly destroys every latency
/// percentile computed over them (ADR 0014).
/// <para>Scenario ids <c>TR01</c>–<c>TR05</c> in <c>docs/test-plan.md</c>.</para>
/// </remarks>
public sealed class TraceLinkTests : IDisposable
{
    private readonly ActivityListener _listener;

    public TraceLinkTests() =>

        // Without a listener an ActivitySource produces nothing at all — which is
        // what makes instrumenting the hot path free when tracing is off, and what
        // makes a test that forgets this pass vacuously.
        _listener = Listen();

    [Fact]
    public void TR01_StartLinked_WithNoSubmissionContext_StartsAnUnlinkedSpan()
    {
        using Activity? activity = RelayTelemetry.StartLinked("test", submissionTraceParent: null);

        // A span, not nothing. Refusing to trace because the correlation is
        // missing would be a strange way to improve observability, and messages
        // submitted before this existed have no context to carry.
        activity.ShouldNotBeNull();
        activity.Links.ShouldBeEmpty();
    }

    [Fact]
    public void TR02_StartLinked_WithAnUnparseableContext_StartsAnUnlinkedSpan()
    {
        using Activity? activity = RelayTelemetry.StartLinked("test", "not-a-traceparent");

        activity.ShouldNotBeNull();
        activity.Links.ShouldBeEmpty();
    }

    [Fact]
    public void TR03_StartLinked_WithASubmissionContext_LinksToIt()
    {
        using Activity submission = StartSubmission();
        string traceParent = submission.Id!;
        ActivityTraceId submissionTrace = submission.TraceId;
        submission.Stop();

        using Activity? dispatch = RelayTelemetry.StartLinked("test", traceParent);

        dispatch.ShouldNotBeNull();

        ActivityLink link = dispatch.Links.ShouldHaveSingleItem();
        link.Context.TraceId.ShouldBe(submissionTrace);
    }

    [Fact]
    public void TR04_StartLinked_StartsANewTraceRatherThanContinuingTheOldOne()
    {
        using Activity submission = StartSubmission();
        string traceParent = submission.Id!;
        ActivityTraceId submissionTrace = submission.TraceId;
        submission.Stop();

        using Activity? dispatch = RelayTelemetry.StartLinked("test", traceParent);

        // The assertion that matters. Sharing a trace id would mean the tracing
        // backend holds one trace open from submission until the last delivery
        // receipt arrives — potentially hours — and computes latency over it.
        dispatch!.TraceId.ShouldNotBe(submissionTrace);
        dispatch.ParentSpanId.ShouldBe(default(ActivitySpanId));
    }

    [Fact]
    public void TR05_StartLinked_DoesNotInheritAnAmbientActivityAsParent()
    {
        using Activity submission = StartSubmission();
        string traceParent = submission.Id!;

        // The submission activity is still current — this is the case where a
        // dispatch happens inside a request, which the API does not do today but
        // an in-process test harness does. The span must still start its own
        // trace, or the behaviour would differ between hosting shapes.
        using Activity? dispatch = RelayTelemetry.StartLinked("test", traceParent);

        dispatch!.TraceId.ShouldNotBe(submission.TraceId);
        dispatch.Links.ShouldHaveSingleItem();
    }

    private static Activity StartSubmission()
    {
        Activity? submission = RelayTelemetry.Source.StartActivity("submission");

        return submission ?? throw new InvalidOperationException(
            "No activity was created, which means the listener is not attached. "
            + "Every assertion below would pass vacuously.");
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RelayTelemetry.SourceName,

            // AllData, not AllDataAndRecorded. Recorded implies a sampling
            // decision to export; these tests care that the span and its links
            // exist, not that anything would ship them.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    public void Dispose() => _listener.Dispose();
}
