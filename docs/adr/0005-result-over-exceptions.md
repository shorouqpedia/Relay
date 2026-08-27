# 5. `Result<T, Error>` for expected failures

Status: Accepted

## Context

There are three ways a .NET codebase typically signals failure, and it is common
to find all three in the same project: throwing an exception, returning a
response envelope with a success flag, and returning a `(value, error)` tuple.
Each is reasonable alone. Together they mean a caller cannot tell from a method
signature how failure will arrive, so callers defend against all three — which is
where handler-wide `try/catch` blocks come from.

The distinction that matters is not "error vs. success". It is **expected vs.
exceptional**. A message routed to a provider that is currently circuit-broken is
an expected outcome of a healthy system; it belongs in the return type. A
`DbUpdateConcurrencyException` is not; it belongs in an exception.

## Decision

One error currency: `Result<T, Error>`, a closed discriminated shape that is
either a value or an `Error`. All expected business outcomes — validation
failure, no eligible provider, rate limit exceeded, duplicate idempotency key,
unknown message — are `Error` values returned from the handler.

Exceptions are reserved for genuinely exceptional conditions and are handled in
exactly one place: the global exception handler.

`Error` is mapped to **RFC 7807 `ProblemDetails`** once, at the API boundary,
with an exhaustive switch. Adding an error category without mapping it is a
compile error.

## Alternatives considered

**Exceptions for everything.** Rejected: it makes control flow invisible in the
signature, and it is expensive on paths that are hit routinely rather than rarely.

**The response envelope pattern** (`ApiResponse.Success(...)` /
`.Failure(...)`). This is the pattern I have used most and it works well in
practice. Rejected here because the envelope is not exhaustively checkable: the
compiler cannot tell you that you forgot to handle a failure case, because
success and failure are the same type with a flag.

**A functional result library.** Rejected — but only just. A library gives more
combinators than a hand-rolled type, and the hand-rolled version will grow toward
it. Written by hand because the type is thirty lines, and because a reviewer
should be able to see exactly what the error contract is without learning a
dependency's idioms.

## Consequences

Handlers get slightly noisier — every fallible call is a check, not a bare call.
That noise is the point: the branches are visible.

Async composition is the weak spot. `Result` inside `Task` needs helpers
(`BindAsync`, `MapAsync`) that a language with proper monadic syntax would not
require. Those helpers exist and they are the ugliest code in the repository.

The rule against catch-all handlers (ADR 0009) only holds because this decision
holds. If expected failures were exceptions, catching them in the handler would
be correct.
