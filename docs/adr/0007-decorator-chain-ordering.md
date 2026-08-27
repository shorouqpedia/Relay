# 7. Provider decorator chain, and its order

Status: Accepted

## Context

Every provider call needs the same treatment: retries with backoff, a circuit
breaker, a client-side rate limit matching the provider's published quota,
metrics, and structured logging. None of that is provider-specific, and none of
it belongs in a provider implementation — a provider assembly should contain
exactly the knowledge of how to talk to one upstream, and nothing else.

## Decision

`IMessageProvider` is wrapped in a decorator chain composed by DI:

```
Logging → Metrics → RateLimit → Resilience → <concrete provider>
```

Each decorator implements the same interface and knows nothing about the others.
Adding a concern is adding a decorator and one registration line.

**The order is the decision, and it is not arbitrary.** Reading outward-in:

- **Logging outermost** so a log line represents one logical delivery attempt.
  Inside the resilience decorator it would emit one line per physical retry, and
  "attempts" in the logs would stop matching "attempts" in the domain.
- **Metrics next**, for the same reason: the latency histogram should measure what
  the caller experienced, including retry time, because that is the number that
  matters to a queue depth. Physical per-try latency is recorded separately, by
  the resilience decorator itself.
- **Rate limit outside resilience**, so a retry does not bypass the quota. This is
  the one that is genuinely easy to get backwards, and getting it backwards turns
  a transient failure into a quota breach — retries hammer an upstream that is
  already refusing. Rate limiting must gate every physical call, so it must sit
  above the thing that generates extra physical calls.
- **Resilience innermost**, adjacent to the real call, so it sees the raw failure
  before any other layer has interpreted it.

## Alternatives considered

**A base class each provider inherits.** Rejected: inheritance fixes the set of
concerns and their order at compile time for every provider, and a provider that
needs to opt out of one has to override its way around the base class.

**Resilience in `HttpClient` handlers only.** Partly used — the resilience
decorator is implemented over `Microsoft.Extensions.Http.Resilience`. Rejected as
the whole answer because not every provider is HTTP; the webhook provider's
failure modes are its own, and the chain has to apply to the abstraction, not the
transport.

**Middleware-style `Func` pipeline.** Rejected: same semantics, worse
debuggability. A decorator gives a named type in the stack trace.

## Consequences

Five layers of indirection between a call and the network. Stack traces are
deeper and a debugger session steps through more frames — an accepted cost that
is genuinely felt when diagnosing something.

The order is load-bearing and invisible at the call site. It lives in one
registration method with a comment pointing here, and there is a test asserting
the composed chain's order so a reordering during a refactor fails loudly rather
than silently changing the rate-limit semantics.
