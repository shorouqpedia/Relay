# 8. At-least-once delivery with idempotent effects

Status: Accepted

## Context

Relay sits between a caller and an unreliable upstream, which means it inherits
every failure mode of both. The ones that matter are the ambiguous ones:

- the caller retries a submission after a timeout, unsure whether the first
  attempt landed
- Relay sends to a provider and the connection drops before the response — the
  message may or may not have been sent
- a provider sends a delivery receipt twice
- a provider sends a receipt for a message Relay has already given up on
- a provider never sends a receipt at all

Exactly-once delivery across a network boundary is not available. What is
available is at-least-once delivery with effects that are safe to repeat, which
is the same thing from the caller's point of view.

## Decision

**At-least-once, everywhere, with idempotency as a first-class domain concept.**

- Every submission carries an `IdempotencyKey` — supplied by the caller or derived
  from the payload. It is a unique index in the database, so a duplicate
  submission is rejected by the storage engine rather than by a check-then-act
  race.
- A duplicate submission returns the **original** result, not a conflict. A caller
  retrying after a timeout wants the outcome, not an error about having asked
  twice.
- Outbound state changes and their events commit in one transaction via a
  **transactional outbox**. There is no window in which a message is marked sent
  but the event is lost, or the reverse.
- Receipt processing is keyed on provider message ID and is a no-op when it has
  been seen. Late receipts are applied; receipts for terminal messages are
  recorded as an audit fact without changing state.
- A **reconciliation sweeper** polls providers for messages that have been
  dispatching past their expected receipt window. This handles the failure mode
  that has no event to react to — silence.

## Alternatives considered

**At-most-once** (send, never retry). Rejected: it converts every transient
network failure into a lost message. For a notification system that is the worse
error.

**Distributed transactions across Relay and providers.** Not available; providers
are third parties over HTTP.

**Trusting provider-side idempotency.** Rejected: some providers offer it, with
differing semantics and differing key lifetimes. Depending on it would put a
provider-specific concern into the core flow — exactly what ADR 0003 exists to
prevent. Where a provider does offer it, the provider assembly uses it as an
optimisation, and the core guarantee does not depend on it.

## Consequences

Duplicate suppression state has to be retained and eventually pruned. The
retention window is a configured value and is a genuine trade-off: too short and
a slow caller retry creates a duplicate send; too long and the table grows without
bound.

The sweeper polls, which means load proportional to outstanding messages rather
than to events. Accepted — silence cannot be subscribed to.

Every write path must be re-entrant. This constrains handler design permanently
and is the single biggest source of complexity in the codebase. It is also the
thing that makes the system trustworthy, so it is where the integration tests are
concentrated: the suite includes tests that deliberately deliver the same message
twice, deliver a receipt twice, and deliver a receipt for an abandoned message.
