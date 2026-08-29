# Architecture Decision Records

Every non-obvious choice in this codebase is recorded here rather than in a
comment above the line it affects.

That split is deliberate. A comment explaining *why* a design is the way it is
has to be re-read by everyone who passes the line, forever, and it goes stale
silently because nothing checks it. An ADR is read once, by someone who is
actually asking the question, and it carries the one thing a comment cannot: the
alternatives that were rejected, and what would have to change for the decision
to be revisited.

So inline comments in this repository are rare and narrow — they explain a line
whose *mechanics* are surprising. Anything explaining a *decision* is here.

## Format

Each record states the context, the decision, the alternatives considered and why
they lost, and the consequences — including the bad ones. A record with no
downside listed is a record that has not been thought about hard enough.

Records are immutable once merged. A decision that changes gets a new record that
supersedes the old one; the old one stays, marked superseded, because the reasoning
that was correct at the time is part of the history.

## Index

| # | Decision | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith-over-microservices.md) | Modular monolith over microservices | Accepted |
| [0003](0003-provider-plugin-architecture.md) | Assembly-per-provider plugin architecture | Accepted |
| [0004](0004-rich-domain-model.md) | Rich domain model over anemic entities | Accepted |
| [0005](0005-result-over-exceptions.md) | `Result<T, Error>` for expected failures | Accepted |
| [0006](0006-minimal-apis-over-controllers.md) | Minimal APIs over controllers | Accepted |
| [0007](0007-decorator-chain-ordering.md) | Provider decorator chain, and its order | Accepted, amended |
| [0008](0008-at-least-once-and-idempotency.md) | At-least-once delivery with idempotent effects | Accepted |
| [0009](0009-no-catch-all-handlers.md) | No catch-all `try/catch`, no `#region` | Accepted |
| [0010](0010-testcontainers-over-in-memory.md) | Testcontainers over in-memory fakes | Accepted |
| [0011](0011-pull-based-pipeline.md) | A pull-based delivery pipeline | Accepted |
| [0012](0012-no-mediator.md) | No mediator | Accepted |
| [0013](0013-inbound-callback-trust.md) | Trusting an inbound callback | Accepted |
| [0014](0014-tracing-across-the-queue.md) | Tracing across the queue: links, not one long trace | Accepted |
| [0015](0015-migrations-are-a-deployment-step.md) | Migrations are a deployment step, not a startup step | Accepted |
