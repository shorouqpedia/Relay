# 15. Migrations are a deployment step, not a startup step

Status: Accepted

## Context

Something has to apply the schema before the application can use it. The
convention in most .NET samples is a line in `Program.cs`:

```csharp
await context.Database.MigrateAsync();
```

It works on a laptop. It stops working the moment there is more than one instance.

Three instances start together after a deploy and all three run migrations
concurrently. EF Core takes an advisory lock, so they do not corrupt each other —
but the two that lose block until the first finishes, which means every instance's
startup time is now the duration of the slowest migration. An index build on a
large table turns a rolling deploy into an outage while every replica sits waiting
to become healthy.

There are worse versions. A migration that fails leaves each instance crash-looping
and retrying it. An instance that starts during a migration someone ran by hand
sees a half-applied schema. And the application needs DDL permission on its own
database permanently, so a compromised application can drop tables — a privilege it
uses for a few seconds per deploy and holds for the rest of its life.

## Decision

**Migrations run as a separate one-shot step, before the application starts.**

In `compose.yaml` that is a `migrate` service the API and worker both
`depends_on: service_completed_successfully`. In a real deployment it is a job
that runs to completion before the rollout begins.

The artifact is an **EF Core migration bundle** — a self-contained executable
produced at build time from the migrations in the assembly. It carries no SDK, no
source, and no `dotnet ef` tooling, which is what makes it runnable in a container
that has none of those.

The application never calls `Migrate()`. It reads and writes; it does not alter.

## Alternatives considered

**`Database.Migrate()` on startup.** Rejected for the reasons above. It is the
default in samples because samples have one instance.

**`EnsureCreated()`.** Rejected outright: it builds the schema from the model and
skips migrations entirely, so it produces a database no migration path can ever
reach. It is a development shortcut that makes the first production deploy the
first time migrations have been exercised.

**Applying migrations from CI, before deploying.** This is the right answer in a
real environment and is what the CI workflow does for a deployed database. The
compose service exists because a local `docker compose up` has no CI, and it runs
the same bundle.

**A dedicated migrator project.** Rejected as unnecessary: the bundle is already
a standalone executable, and a project would be a wrapper around the tool that
produces it.

## Consequences

The schema and the application version are decoupled, which means both directions
of skew are now possible and have to be thought about. A migration must work
against the currently running code as well as the new code, because during a
rolling deploy both are live — so a column rename becomes add, backfill, switch,
drop across several releases rather than one `RenameColumn`.

That is a real constraint and the main cost of this decision. It is also true of
any system that deploys without downtime; startup migrations hide it rather than
removing it.

Local startup gains a step. `docker compose up` handles it, and the dependency is
declared, so nothing starts against an unmigrated database.

The application's database user no longer needs DDL rights. Splitting the
credentials — a migration user that can alter, an application user that cannot —
is the natural follow-on and is not done here; the compose setup uses one user
for simplicity, which is a gap worth naming rather than leaving implied.
