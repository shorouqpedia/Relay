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

> **Status: in progress.** The system runs end to end: `docker compose up`
> brings up the API, two workers, PostgreSQL, stand-in upstreams, and Jaeger, and
> a submitted message is delivered, reported on by a signed callback, and marked
> delivered. 208 tests. CI is the remaining piece. See [Roadmap](#roadmap).

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
   ┌─────────────┬───────┴───────┬─────────────┬──────────────┬─────────┐
   │ email.postal│ email.mailhook│ sms.twinkle │ push.beacon  │ webhook │
   └─────────────┴───────────────┴─────────────┴──────────────┴─────────┘
     one assembly each, discovered at startup, never named in the host
```

### Adding a provider edits no production code

Providers are separate assemblies implementing a contract, registering themselves
through `IProviderModule`, and referenced by the hosts via a wildcard rather than
by name. Creating `src/Relay.Providers.<Channel>.<Name>/` is the entire procedure.

This is the claim the project exists to demonstrate, so it is checkable rather
than asserted. The fifth provider was added in its own commit; `git show --stat`
on it shows the only file changed under `src/` is that provider's own. No router,
no composition root, no host project file.

It is worth being precise about what the same commit *did* touch, because it is
more interesting than a clean result would have been. Two shared test files
changed, and one of those changes was the fifth provider proving the contract
wrong — see below.

### The provider contract is enforced, not documented

`Relay.Providers.ContractTests` is one abstract suite that every provider runs.
A provider supplies an instance and a controllable upstream, and adds test
methods only for behaviour genuinely its own.

Most of the assertions cover the unhappy half of the contract, because that is
the half that is easy to get wrong and impossible to notice — a provider that
leaks a vendor exception works perfectly right up until its upstream has an
outage. Five providers run it, and they are deliberately unalike: one is
declarative over Refit, one hand-written over `HttpClient`, one answers HTTP 200
for failures with the real outcome in the body, one reports nothing after
accepting a message, and one posts to an address the recipient supplied and signs
the request.

The suite has paid for itself twice.

It caught a timeout being misclassified as a transient failure in the first
provider — the difference between "this message was not sent" and "this message
may have been sent", and therefore between a free retry and a possible duplicate.

Then the fifth provider failed a scenario that four others passed, and was right
to. `PC09` required an unparseable response to be reported as a transient
failure, which quietly assumed every provider reads a body to decide whether it
succeeded. The webhook provider does not — the status line is its entire answer —
so for it a 200 with a malformed body is a real success. The shared scenario now
states only what holds for all of them, and the stronger claim moved to the four
subclasses where it is true.

That is the rule working as written: when a provider cannot pass the suite, the
first suspect is the abstraction. Adding an opt-out flag instead would have kept
a false claim and made the contract slightly less shared.

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
docker compose up --build
```

That is the whole thing: PostgreSQL, the API, two workers, the stand-in
upstreams, and Jaeger. No accounts, no API keys, nothing leaves the machine.

- API — http://localhost:5080 (`/scalar` for the OpenAPI reference)
- Traces — http://localhost:16686
- Provider controls — http://localhost:5099

The workers run as **two replicas** deliberately. Row-level claiming and the
concurrency token exist for exactly that configuration, so running one would
leave the parts of the design that matter most unexercised.

### Watching it fail

A stack where every provider always works demonstrates nothing. The stand-in
upstreams can be told to misbehave:

```bash
curl -X POST http://localhost:5099/_control/postal/behaviour -H "Content-Type: application/json" -d '{"behaviour":"server_error"}'
```

Submit a message after that and the delivery history shows the first attempt
failing on `email.postal` and the second succeeding on `email.mailhook` — routing
excluding a provider this message has already failed on.

`"behaviour":"silent"` is the interesting one: the upstream accepts messages and
then never reports anything. The message sits in `Sent` until its provider's
receipt window elapses and the sweeper abandons it, which is the failure mode that
produces no event to react to.

```bash
curl -X POST http://localhost:5080/api/v1/messages -H "Content-Type: application/json" -H "Idempotency-Key: demo-0001" -d '{"channel":"Email","recipient":"someone.com","body":"Hello.","subject":"Relay"}'
```

### Tests

```bash
dotnet test
```

Unit and contract tests need nothing but the SDK. Integration tests need Docker —
they start PostgreSQL as a container rather than expecting a pre-provisioned
server, so a fresh clone can run them
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
| Remaining providers, incl. the zero-edit demonstration | Done |
| Callbacks: HMAC verification, receipts, reconciliation | Done |
| Observability: OpenTelemetry, health checks | Done |
| Containers and one-command startup | Done |
| CI: build, test, security scanning, SBOM | Next |

## What I would do differently

To be written once there is enough built to be honest about. A section that
appears before the mistakes do is a marketing section.
