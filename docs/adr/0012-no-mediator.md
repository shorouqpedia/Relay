# 12. No mediator

Status: Accepted

## Context

Every service I have built puts MediatR between the transport and the use cases:
a request type, a handler, and pipeline behaviors for validation and logging.
Controllers bind, send, and return. It works, it is consistent, and it is what I
would reach for without thinking.

Writing this project's HTTP surface is the moment to ask what it was buying.

A mediator gives three things. **Decoupling** — the caller does not name the
handler. **A pipeline** — cross-cutting behaviour runs around every request.
**Uniformity** — every use case has the same shape.

The first is worth examining. In a service like this the only caller of a handler
is the endpoint immediately above it, and there is exactly one. The indirection
does not remove a dependency; it hides one, and it costs the reader the ability
to reach a handler by clicking its call site. "Find usages" on a handler in a
MediatR codebase returns nothing.

The second is real, and it is the actual reason the pattern earns its place with
controllers — an `IPipelineBehavior` is the only clean way to run validation
before every handler. But minimal APIs already have that mechanism.
`AddEndpointFilter` on a `MapGroup` runs before every endpoint in the group, and
unlike a behavior it is visible in the group declaration rather than in a
registration somewhere else.

The third stands regardless of how handlers are invoked.

## Decision

No mediator. Endpoints call handlers directly, as injected classes.

Cross-cutting concerns are endpoint filters on the group:

```csharp
RouteGroupBuilder messages = app
    .MapGroup("/api/v{version:apiVersion}/messages")
    .AddEndpointFilter<ValidationFilter>()
    .AddEndpointFilter<IdempotencyFilter>();
```

Everything that runs before an endpoint is listed above it, in one place.

Handlers keep the shape a mediator would have imposed — one class per use case,
one public method, a `Result` return — because that part was never about the
mediator.

## Alternatives considered

**MediatR, as in my other services.** Rejected here specifically because the
pipeline argument, which is what justifies it, is answered by a framework feature
this project is already using. Keeping it would mean two pipeline mechanisms in
one codebase, and a reader would have to check both to know what runs before a
given endpoint.

There is also a licensing dimension: MediatR moved to a commercial licence for
larger organisations. Not the deciding factor — a project this size would remain
free — but it is a real consideration for something that would otherwise sit at
the centre of an application's structure.

**A hand-rolled dispatcher.** Rejected as the worst of both: the indirection of a
mediator, with none of the ecosystem, and one more thing to explain.

## Consequences

Endpoints name their handlers. That is more coupling on paper and less in
practice, since the relationship was one-to-one either way — and it means the
path from a route to the code that serves it is a click rather than a search.

Adding a cross-cutting concern means adding a filter to the groups that need it,
rather than one behavior that silently applies everywhere. More explicit, and
more places to forget. The mitigation is that groups are few and their
declarations sit together.

This decision would not survive the surface growing several times over, or a
second transport arriving. A mediator's decoupling starts paying the moment a use
case has more than one caller — a queue consumer and an endpoint invoking the same
handler is exactly that case. If that happens, this record gets superseded rather
than argued with.
