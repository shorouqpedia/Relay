# Relay

A multi-provider notification delivery service in .NET 10.

Relay accepts a message, chooses a provider for its channel, delivers it, and
deals with everything the other side does afterwards — including the parts that
are silence. Late delivery receipts, duplicate receipts, receipts for messages
already abandoned, and providers that accept a message and never mention it
again.

The interesting problem here is not sending an email. It is that the systems on
the other end are unreliable in slow, partial, ambiguous ways, and the design has
to be honest about what it does and does not know.

> **Status: in progress.** The domain, the provider contract, the first
> provider, persistence, the delivery pipeline, and the HTTP surface are built
> and tested — 124 tests, 26 of which drive the real host against PostgreSQL in
> a container. The remaining providers, callbacks, observability, containers,
> and CI are not built yet. See [Roadmap](#roadmap).

## Why this exists

This is a portfolio project, and it is aimed at a specific target: the parts of a
distributed system that only show up under failure. Every design decision that
was not obvious is written down in [`docs/adr/`](docs/adr/) — including the ones
that went against my own prior habits, and why.

If you only read one thing, read
[ADR 0008](docs/adr/0008-at-least-once-and-idempotency.md) on at-least-once
delivery, and then the `DR` scenarios in [`docs/test-plan.md`](docs/test-plan.md).
That pair is the argument the whole system rests on.

## Design in one screen

```
                  ┌──────────────┐
   HTTP  ────────►│  Relay.Api   │  minimal APIs, one group per resource
                  └──────┬───────┘
                         │  submit / cancel / query
                  ┌──────▼───────┐
                  │ Application  │  use cases, validation, routing policy
                  └──────┬───────┘
                         │
                  ┌──────▼───────┐
                  │   Domain     │  Message aggregate, value objects,
                  └──────────────┘  the lifecycle state machine

                  ┌──────────────┐
                  │ Relay.Worker │  delivery pipeline, outbox dispatch,
                  └──────┬───────┘  receipt reconciliation sweeper
                         │
              ┌──────────▼──────────┐
              │  decorator chain    │  logging → metrics → rate limit
              │                     │  → resilience → provider
              └──────────┬──────────┘
                         │
   ┌─────────────┬───────┴───────┬─────────────┬──────────────┐
   │ email.postal│ email.mailhook│ sms.twinkle │ push.beacon  │  …
   └─────────────┴───────────────┴─────────────┴──────────────┘
     one assembly each, discovered at startup, never named in the host
```

### Adding a provider changes no existing file

Providers are separate assemblies implementing a contract, registering themselves
through `IProviderModule`, and referenced by the hosts via a wildcard rather than
by name. Creating `src/Relay.Providers.<Channel>.<Name>/` is the entire procedure.

This is the claim the project is built to demonstrate, so it is verifiable rather
than asserted: the last provider was added in its own commit, and `git show` on
it touches only new files.

### The provider contract is enforced, not documented

`Relay.Providers.ContractTests` is one abstract suite that every provider runs.
A provider supplies an instance and a controllable upstream; it adds no test
methods of its own.

Most of the assertions cover the unhappy half of the contract, because that is
the half that is easy to get wrong and impossible to notice — a provider that
leaks a vendor exception works perfectly right up until its upstream has an
outage. The suite was worth its cost immediately: it caught a timeout being
misclassified as a transient failure in the first provider, which is the
difference between "this message was not sent" and "this message may have been
sent", and therefore the difference between a free retry and a possible duplicate.

If a provider cannot pass the suite, the conclusion is that the abstraction is
wrong — not that the interface needs an opt-out flag.

## Repository layout

```
src/
  Relay.Domain/                  no references, to anything, on purpose
  Relay.Application/             references Domain only
  Relay.Providers.Abstractions/  the plugin contract
  Relay.Providers.*/             one assembly per provider
  Relay.Infrastructure/          persistence, caching, outbox
  Relay.Api/                     minimal APIs
  Relay.Worker/                  delivery pipeline
tests/
  Relay.Domain.UnitTests/        the state machine and the value objects
  Relay.Application.UnitTests/
  Relay.Providers.ContractTests/ the suite every provider must pass
  Relay.IntegrationTests/        real HTTP against real infrastructure
tools/
  Relay.FakeProviders/           upstreams that misbehave on request
docs/
  adr/                           why, for everything that was a choice
  test-plan.md                   the scenarios test names refer to
```

## Running it

```bash
dotnet test
```

Unit and contract tests need nothing but the SDK. Integration tests need Docker —
they start PostgreSQL, Redis, and a broker as containers rather than expecting a
pre-provisioned server, so a fresh clone can run them
([ADR 0010](docs/adr/0010-testcontainers-over-in-memory.md)).

## Build gates

`TreatWarningsAsErrors` and transitive NuGet auditing are on from the first
commit. Both were cheap that day and get more expensive every week they are
deferred, so there was never going to be a better moment.

Analyzer rules that are switched off are switched off in `.editorconfig` with a
written reason next to each. A rule disabled without one is a rule that was
inconvenient, which is not the same thing.

## Roadmap

| Milestone | Status |
|---|---|
| Solution skeleton, build gates, ADRs | Done |
| Domain: aggregate, value objects, state machine | Done |
| Provider contract and its test suite | Done |
| First provider (`email.postal`) | Done |
| Persistence: EF Core mapping, migrations, outbox | Done |
| Delivery pipeline: dispatch, reconciliation, recovery loops | Done |
| Decorator chain: resilience, rate limiting, metrics | Done |
| HTTP surface: minimal APIs, ProblemDetails, versioning | Done |
| Remaining providers, incl. the zero-edit demonstration | Next |
| Callbacks: HMAC verification, receipts, reconciliation | |
| Observability: OpenTelemetry, health checks | |
| Containers and one-command startup | |
| CI: build, test, security scanning, SBOM | |

## What I would do differently

To be written once there is enough built to be honest about. A section that
appears before the mistakes do is a marketing section.
