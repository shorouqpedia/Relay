# 10. Testcontainers over in-memory fakes

Status: Accepted

## Context

Integration tests need a database, a cache, and a broker. There are three ways to
supply them: the EF Core in-memory provider, a shared pre-provisioned server, or
ephemeral containers per test run.

My prior work uses the second — integration tests running against a real database
that has to exist before the suite runs. The fidelity is excellent and the
portability is poor: a fresh clone cannot run the tests, and state leaks between
runs unless every test cleans up perfectly.

The in-memory provider is portable and low-fidelity in exactly the places this
project cares about. It does not enforce unique indexes the way a relational
engine does, which is precisely the mechanism ADR 0008 relies on for duplicate
suppression. A test suite that passes against it would prove nothing about the
guarantee that matters most.

## Decision

Testcontainers. PostgreSQL, Redis, and RabbitMQ start as containers for the test
run, shared across a test collection, and are discarded afterwards.

`git clone && dotnet test` works on a machine with Docker and nothing else
installed.

## Alternatives considered

**EF Core in-memory provider.** Rejected for the reason above: it cannot enforce
the constraint the design depends on, so it would give a green suite and a false
guarantee.

**SQLite in-memory.** Closer, and fast. Rejected because the production provider
is PostgreSQL and the differences that would bite are exactly the interesting
ones — concurrent update semantics, JSON column behaviour, and index enforcement
under contention.

**Shared pre-provisioned server.** Rejected: it is what I have done before, and
its cost is that the test suite is not self-contained. It also serialises CI runs
against a shared resource.

## Consequences

Docker becomes a hard prerequisite for running the integration suite. Unit tests
and domain tests have no such dependency and are the fast inner loop.

The suite is slower — container startup is measured in seconds. Mitigated by
sharing containers across a collection rather than per test, and by keeping the
number of tests that genuinely need infrastructure small.

CI needs a Docker-capable runner. The GitHub-hosted Linux runners provide this,
so the cost is zero there.
