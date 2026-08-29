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

> **Status: complete enough to read.** `docker compose up` brings up the API,
> two workers, PostgreSQL, stand-in upstreams, and Jaeger; a submitted message is
> delivered, reported on by a signed callback, and marked delivered. 208 tests,
> and CI verifies the claims on this page rather than trusting them. What is left
> is written down under [What I would do differently](#what-i-would-do-differently).

## Why this exists

This is a portfolio project, and it is aimed at a specific target: the parts of a
distributed system that only show up under failure. Every design decision that
was not obvious is written down in [`docs/adr/`](docs/adr/) — including the ones
that went against my own prior habits, and why.

### If you are reviewing this, read these four things

In order. They take about fifteen minutes and cover the parts worth judging.

1. **[ADR 0008 — at-least-once delivery](docs/adr/0008-at-least-once-and-idempotency.md)**,
   then the `DR` scenarios in [`docs/test-plan.md`](docs/test-plan.md). This pair
   is the argument the whole system rests on, and the tests are where it becomes
   concrete: a duplicate receipt succeeds, and a true receipt arriving after the
   sweeper gave up is still refused.

2. **[`Message.cs`](src/Relay.Domain/Messaging/Message.cs)** — the aggregate.
   Every state change is a named method that checks the transition first, so an
   invalid state is not guarded against at call sites; there is no API through
   which it can be reached.

3. **[`ProviderContract.cs`](tests/Relay.Providers.ContractTests/ProviderContract.cs)** —
   one suite, five deliberately unalike providers. Most of it is about the
   unhappy half of the contract, which is the half that is easy to get wrong and
   impossible to notice.

4. **[ADR 0011 — a pull-based pipeline](docs/adr/0011-pull-based-pipeline.md)**.
   The two failures that matter most here are defined by absence — a worker that
   died mid-dispatch, a provider that went silent — and neither produces an event
   to react to.

The ADRs record what was rejected and why, including several decisions that went
against my own habits. [ADR 0007](docs/adr/0007-decorator-chain-ordering.md)
carries an amendment written when building it proved the original choice wrong,
and [ADR 0012](docs/adr/0012-no-mediator.md) explains why a pattern I use in
every other service is absent from this one.

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
build/
  docker/                        one Dockerfile, four targets
  check-style.sh                 the rules .editorconfig cannot express
  check-config.sh                no secrets in committed configuration
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

### CI checks the claims on this page

Every assertion this repository makes about itself is verified in CI, or it is
not made ([ADR 0016](docs/adr/0016-ci-verifies-the-claims.md)). Prose does not
fail, so a claim that stops holding decays quietly into a lie — and documentation
that confidently describes something the code no longer does is worse than none,
because a reader cannot tell which parts are still true.

- `build/check-style.sh` fails on `#region` and on an unfiltered
  `catch (Exception)`. Neither is expressible in `.editorconfig`, which is why
  ADR 0009 previously named a script that did not exist — a claim about
  enforcement that was itself unenforced.
- `build/check-config.sh` fails when a committed settings file gives a
  secret-bearing key a value, or when a connection string carries a password.
- The integration tests run against a real PostgreSQL, and migrations are applied
  to a throwaway database so a migration that does not apply fails here rather
  than at deploy time.
- `docker compose up` is exercised on every push: a message is submitted, and CI
  fails unless it reaches `Delivered` — through the worker, the upstream, and a
  signed callback. If the README's first instruction stops working, the build
  goes red.

CodeQL, secret scanning over the full history, container image scanning, and SBOM
generation run in a separate workflow, on a schedule as well as on push — an
advisory published against a package that has been in the graph for months
becomes true without anyone changing anything.

## What is not here

Stated plainly, because a reader should not have to discover a gap by looking for
something and failing to find it.

- **No authentication on the API.** Submission is open. Real deployment needs a
  scheme, and the shape of it — per-tenant keys, quotas, and which messages a
  caller may read back — would change the domain, not just the edges.
- **No broker.** The outbox exists, is transactional, and is dispatched by a
  loop; the publisher writes to the log. Swapping it is one registration line,
  which is the point of the interface, but nothing has proven that.
- **No multi-tenancy.** One set of provider credentials, one routing policy.
- **No callback secret rotation.** Rotating one breaks that provider's callbacks
  until both sides are updated. Accepting either of two secrets during a rotation
  is the standard answer and is recorded as a known gap in ADR 0013 rather than
  quietly omitted.
- **One database user.** It holds DDL rights because the compose setup shares a
  credential between the migration bundle and the application. Splitting them is
  the natural follow-on to ADR 0015 and is not done.
- **No load testing.** Nothing here has met contention beyond two workers and a
  handful of messages, so every claim about throughput is a claim about design
  rather than measurement — and this README makes none.

## What I would do differently

Written after the fact, which is the only time it is worth anything.

**I would have tested that provider discovery works, at milestone two.** The
worst bug in this project was live for three milestones: discovery read
`Assembly.GetEntryAssembly().GetReferencedAssemblies()`, which under a test
runner is the runner — so the API ran with zero providers and every test passed
anyway, because none of them needed one. It was silent by construction, since
"discovery found nothing" is indistinguishable from "there is nothing to find".

The lesson generalises past this bug: **a mechanism whose failure mode is
emptiness needs a test that something was found.** I had tests for what each
provider does and none for whether any provider exists.

**I would have started the compose stack much earlier.** Standing it up at
milestone ten found four defects in an afternoon — a captive dependency, a
misregistered service, three providers building URLs that silently dropped their
path prefix, and an `.editorconfig` missing from the image so the container built
with different analyzer rules than my machine. None of them were reachable from a
unit test, and all of them had been sitting there for weeks. Containers were the
last milestone because they were the biggest gap in my experience; that was
exactly the wrong reason to defer them.

**I would not have written `PC09` the way I did.** The contract asserted that an
unparseable response must be reported as a transient failure, which quietly
assumed every provider reads a body to decide whether it succeeded. Four
providers passed. The fifth was right to fail. The assumption was invisible until
something broke it, and the fix — narrowing the shared claim and moving the
sharper one to where it is true — is what the suite should have said from the
start.

**The zero-edit claim needed qualifying, and I would state it correctly first.**
Adding a provider edits no production code, which is the useful and checkable
version. My original phrasing was "changes no existing file", and adding the
fifth provider changed two shared test files — one because a generated
configuration needed a new key, one because the contract was wrong. Overstating
it made a true and interesting property sound like a claim that had failed.

**I would use fewer packages and add them later.** Seven package references were
declared and never used — Redis, HybridCache, feature management, a resilience
library, a mocking library, two Testcontainers modules. They came from planning
what the project would need rather than from needing them, and each one is audit
noise, attack surface, and a false signal about what the system does. One of them
had a comment explaining a decision that a later ADR amendment had already
reversed.

**The comment density is still higher than it should be.** The intent was to move
architectural reasoning into `docs/adr/` and leave short comments explaining
mechanics — and the ADRs did absorb most of it. But several files still carry
three-paragraph remarks where two sentences and a link would do. The habit is
useful in code someone inherits and reads as noise in code someone is evaluating,
and I have not fully recalibrated.

**Some of what I built is more machinery than this domain needs.** The rich
aggregate earns its place — the lifecycle has real invariants. The decorator
chain earns its place. But four value objects, an outbox, and a
sixteen-project solution for a service with one aggregate and five endpoints is
a demonstration first and a proportionate design second. In a product I would
have started smaller and let the seams appear.

## Roadmap

Everything planned is built.

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
| CI: build, test, security scanning, SBOM | Done |
