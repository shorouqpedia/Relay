# 11. A pull-based delivery pipeline

Status: Accepted

## Context

Something has to take messages from `Pending` to a provider. The obvious design,
given that the outbox already publishes events, is to have the worker subscribe:
`MessageQueued` arrives on the broker, a consumer picks it up and delivers it.

That design has a failure mode it cannot recover from on its own. A message
reaches `Pending` and then the event is lost — the broker drops it, a consumer
acknowledges and crashes, a subscription is misconfigured for an hour. The
message row is still there, correct and complete, and nothing will ever look at
it again. The system has no way to notice, because nothing is watching the table;
it is watching the stream.

The same applies to every other state a message can get stuck in. A worker that
dies mid-dispatch leaves a row in `Dispatching` that no event describes. A
provider that accepts a message and never sends a receipt leaves a row in `Sent`
that no event describes. Neither has anything to subscribe to, because the thing
that happened is that nothing happened.

## Decision

**The database is the queue.** The worker polls for claimable work rather than
consuming an event stream.

Three loops, each claiming with `FOR UPDATE SKIP LOCKED`:

- **dispatch** — claims `Pending` messages and delivers them
- **reconciliation** — claims `Sent` messages past their provider's receipt
  window and either asks the provider or gives up
- **recovery** — claims `Dispatching` messages older than any provider call could
  take, and returns them to the queue

The outbox still exists and still publishes, but its events are for *other
people* — consumers outside Relay reacting to a delivery — not for Relay's own
pipeline. Nothing internal depends on an event arriving.

The rule this follows: **state is the source of truth, events are a notification.**
A system whose own correctness depends on its notifications arriving has made a
delivery guarantee it does not control.

## Alternatives considered

**Broker-driven consumers.** Rejected for the reason above: it makes the
pipeline's liveness depend on message delivery, and it has no answer at all for
the two conditions that are defined by absence — the stuck dispatch and the
missing receipt. Those would need a polling sweeper anyway, at which point the
system has two mechanisms doing one job, and the interesting failures land in the
gap between them.

**Polling plus a notification to reduce latency** (`LISTEN`/`NOTIFY`, or an event
that wakes the loop early). Genuinely attractive, and the intended next step if
latency ever matters: it keeps the poll as the guarantee and uses the
notification only as an optimisation, so losing a notification costs latency
rather than a message. Not built now because the added machinery is only worth it
once someone is actually waiting.

**A hosted `BackgroundService` per loop with a fixed delay.** This is what is
built — the alternative was one loop doing all three jobs on one schedule.
Rejected because the three have genuinely different cadences: dispatch wants to
run constantly, reconciliation wants to run every few minutes, and recovery is
rare. One schedule would mean either polling the database for stuck dispatches
every second or delaying every message by the reconciliation interval.

## Consequences

The database takes constant polling load, proportional to worker count rather
than to traffic. Mitigated by the partial indexes — each loop's query touches a
small, shrinking slice of its table rather than scanning it — and by backing off
when a poll finds nothing.

Latency has a floor: a message waits up to one poll interval before anyone looks
at it. For a notification system that is an acceptable trade, and it is the cost
that the `LISTEN`/`NOTIFY` option above would buy back.

Every loop must be safe to run in several instances at once. That is what
`SKIP LOCKED` provides, and it is asserted in the persistence suite rather than
assumed.

There is no separate dead-letter queue. A message that cannot be delivered ends
in `DeadLettered` in the same table, which means the operational question "what
failed" is a query rather than an inspection of another system.
