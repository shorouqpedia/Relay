# 14. Tracing across the queue: links, not one long trace

Status: Accepted

## Context

A message is submitted over HTTP, sits in the database, and is dispatched later
by a different process. Between the two there may be milliseconds or hours, and
the delivery may be attempted three times against two providers.

The instinct is to make that one trace: propagate the W3C `traceparent` from the
submission request onto the message row, and have the worker continue that trace
when it dispatches. Every span from submission to delivery under one trace id,
one flame graph, the whole story in one view.

That produces traces that break the tools meant to display them.

A trace is a unit of *work with a beginning and an end*, and tracing backends
assume it completes in something like request time. A trace that starts on
Tuesday and ends on Wednesday when a bounce arrives has:

- **an unbounded duration**, so latency percentiles computed over traces become
  meaningless — the p99 of "submission to final outcome" is dominated by messages
  waiting for an email bounce that takes six hours
- **no completion**, so the backend either holds it open indefinitely or flushes
  it in pieces and shows a broken tree
- **spans that outlive their parent's retention**, so the submission span is
  already expired when the delivery span arrives and the tree cannot be rebuilt

The deeper problem is that the causal relationship here is not *containment*. The
worker's dispatch is not part of the API's request. The request ended, having
recorded a durable intention. The dispatch happens because of it, later,
independently.

## Decision

**Each phase is its own trace, joined by span links.**

- The submission request's trace context is recorded on the message when it is
  accepted.
- When a worker dispatches that message, it starts a **new trace** with an
  `ActivityLink` back to the submission's context.
- The same applies to an inbound callback: processing a receipt is its own trace,
  linked to the submission that produced the message.

Links are exactly the OpenTelemetry construct for this — a causal reference
between spans in different traces, with no parent-child lifetime relationship.
A tracing backend renders them as a jump between traces rather than as a branch
of one.

The trace context is stored as a **shadow property** on the message row, not as a
property of the aggregate. It is metadata about how a message came to exist, not
part of what a message *is* — the domain has no opinion about distributed tracing
and gains nothing from being able to see it.

## Alternatives considered

**One continuous trace.** Rejected for the reasons above. It is the option that
looks best in a demo with one fast message and degrades badly in exactly the
conditions this system is built for: slow providers, long receipt windows,
retries.

**No correlation at all — separate traces, joined by the message id in logs.**
Genuinely viable, and what many systems do. Rejected because the join then only
exists in a log query, so a tracing UI cannot follow it, and the person debugging
has to know to look. Links cost one field on a row and make the connection
navigable.

**Correlating on the message id as a trace id.** Rejected: a trace id is not an
identifier of a business object, and reusing it as one collides with every retry
producing spans that claim to be the same trace, hours apart.

## Consequences

A single view of "everything that happened to this message" requires following
links across several traces rather than opening one. That is a real cost in
convenience, paid to keep each trace a well-formed unit of work. The message id
is on every span as an attribute, so a search still gathers them.

The trace context has to be persisted, which means a column and a shadow-property
mapping. The domain does not see it and no test of the domain mentions it.

A message submitted before this was built, or dispatched by a worker that lost
the context, produces a dispatch trace with no link. That is handled as an absence
rather than an error — an unlinked trace is still a useful trace.
