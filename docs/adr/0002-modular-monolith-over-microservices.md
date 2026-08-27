# 2. Modular monolith over microservices

Status: Accepted

## Context

Relay routes a submitted message to one of several interchangeable delivery
providers, tracks the outcome, and reconciles delivery receipts that arrive late
or not at all. The obvious modern instinct is to split this into services — a
submission API, a routing service, a delivery worker, a receipt processor.

The domain does not justify it. There is one consistency boundary (a message and
its delivery attempts), one team, and one deployment cadence. The parts that
genuinely need to scale independently are the API and the delivery pipeline, and
those are already separate *processes* without being separate *systems*.

## Decision

One codebase, two deployable processes (`Relay.Api`, `Relay.Worker`), sharing a
database and communicating through a queue and an outbox.

Module boundaries are enforced in the build — assembly references express the
dependency rule, and the domain assembly is provably reference-free — so the
seams are real even though they are not network seams.

## Alternatives considered

**Microservices per channel.** Rejected: it would split on the wrong axis.
Channels differ in *delivery mechanism*, which is the thing the provider
abstraction already isolates. Splitting there would create four services that
share nearly all their logic and differ only in an adapter.

**A single process.** Rejected: the delivery pipeline is long-running, bursty,
and needs to scale on a different signal than request traffic. Keeping it inside
the API process would couple those, and would make a slow provider degrade
request latency.

## Consequences

The seams are checkable but not enforced by the network, so discipline has to
come from the build and from review. That is a real risk and the honest reason
most teams reach for microservices: they buy enforcement with operational cost.

If a genuine need to split arrives, the assembly boundaries are the extraction
points, and the outbox is already in place — the message flow does not have to
be redesigned to become a network flow.

This decision costs the project a demonstration of inter-service tracing and
distributed transactions. That is a deliberate trade: a well-argued monolith is
better evidence of judgement than an unnecessary distributed system.
