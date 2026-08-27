# 6. Minimal APIs over controllers

Status: Accepted

## Context

Relay's HTTP surface is small: submit a message, fetch its status, list attempts,
receive a provider callback, and a handful of operational endpoints. Every
endpoint is a thin translation between HTTP and a handler call.

Controllers carry machinery this surface does not use — a base class, action
filters, model binding conventions, an action selector — and the cost of that
machinery is indirection: to know what runs before an endpoint you consult
registration order, attributes, and filter scopes across several files.

## Decision

Minimal APIs, organised with `MapGroup` per resource, with cross-cutting concerns
attached as endpoint filters on the group.

Each endpoint is a named static method in a feature folder, not a lambda inline
in `Program.cs`. Lambdas are how minimal APIs get a reputation for being
unstructured, and the reputation is earned when everything lives in one file.

Groups carry versioning, authorization policy, validation, and idempotency
filters, so an endpoint's full pipeline is visible in the group declaration
directly above it.

## Alternatives considered

**Controllers.** This is what I know best and what all my prior services use.
Rejected here specifically because it is what I already have evidence of. There
is nothing wrong with controllers for this surface — with a shared base class and
action filters the result would be comparable. The deciding factor is that
minimal APIs are the default for new .NET services and my experience of them is
zero; a portfolio should close that.

**FastEndpoints.** Rejected: it is good, but it substitutes a third-party
convention for a framework one, and a reviewer learns less about what I
understand from a framework than from what I do with the primitives.

## Consequences

Some things controllers give free need building: content negotiation beyond JSON,
and conventional route tokens. Neither is needed here.

OpenAPI metadata is explicit — `.Produces<T>()`, `.ProducesProblem()` — which is
more typing and a more accurate document than attribute inference produces.

If the HTTP surface grew several times over, the group declarations would become
the new place complexity accumulates, and controllers would start winning again.
That is the signal to revisit this.
