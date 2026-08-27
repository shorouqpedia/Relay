# 4. Rich domain model over anemic entities

Status: Accepted

## Context

The default .NET shape for an entity is a class of public auto-properties,
populated by object initialiser in a handler, with the business rules living in
the handler or in a service beside it. It is fast to write, it maps to EF Core
without thought, and for genuinely CRUD-shaped systems it is often the right
answer.

Relay is not that shape. A message has a lifecycle with real invariants:

- a message that has been delivered can never move back to pending
- an attempt can only be recorded against a message that is currently dispatching
- the attempt count can never exceed the configured maximum
- a dead-lettered message has a reason, always, and it is immutable afterwards

With public setters, every one of those rules is enforceable only by whoever
remembers to check. The compiler permits `message.Status = Delivered` from
anywhere, so correctness depends on no caller ever doing the obvious thing.

## Decision

`Message` is an aggregate root: private collections exposed as
`IReadOnlyCollection<T>`, no public setters, no public parameterless constructor.
State changes happen through intention-named methods (`MarkDispatching`,
`RecordAttempt`, `Deliver`, `DeadLetter`) that validate the transition and raise
a domain event.

Illegal transitions are unrepresentable rather than merely rejected: there is no
public API through which the invalid state can be reached, so it does not need to
be guarded against at every call site.

Value objects carry the rules that primitives cannot: `Recipient` validates its
own format per channel, `IdempotencyKey` normalises, `MessageBody` enforces
per-channel size limits.

EF Core maps this using owned types, backing fields, and value converters. The
model does not bend for the ORM — that is the point of the exercise.

## Alternatives considered

**Anemic entities with a domain service layer.** This is the pattern I have most
experience with, and it works: the rules live in named services rather than
smeared across handlers, which is far better than nothing. Rejected here because
it still permits the invalid state to exist in memory — the service is a
convention, not a boundary — and because demonstrating the alternative is part of
the point of this project.

**Separate persistence models and domain models.** Rejected as premature. It
solves a real problem (ORM constraints distorting the model) that owned types and
backing fields already solve at this scale, at the cost of a mapping layer whose
only job is to exist.

## Consequences

Test setup becomes harder: you cannot object-initialise a `Message` into an
arbitrary state. This is a genuine cost and it is paid with test data builders
that walk the aggregate through legal transitions — which has the side effect of
making the tests exercise the transitions rather than assume them.

EF Core configuration is more involved. Four value converters and three owned
types is real complexity that an anemic model would not have.

Serialization needs care — the aggregate is not a DTO and is never returned from
an endpoint.

The honest counter-argument: for a system whose core operation is "write a row,
call an API, write another row", this is more machinery than the domain strictly
demands. It is justified here by the lifecycle invariants, which are real. It
would not be justified for the audit log, which is why the audit log is a flat
append-only record and not an aggregate.
