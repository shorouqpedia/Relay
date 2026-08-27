# 3. Assembly-per-provider plugin architecture

Status: Accepted

## Context

Relay delivers through providers that differ in almost every respect: transport
(REST, webhook callback), authentication (bearer token, HMAC-signed request,
mutual TLS), payload shape, rate-limit semantics, error vocabulary, and how — or
whether — they report final delivery.

What they share is the orchestration around them: pick one, attempt delivery,
classify the outcome, retry or fail over, record the attempt, reconcile the
receipt. That flow is identical regardless of who is on the other end.

This is the shape that makes or breaks an integration codebase. Get the seam
wrong and every new provider leaks its peculiarities upward — a special case in
the router here, an extra nullable field on the request there — until the
"abstraction" is a union of every provider's quirks.

## Decision

Each provider is its own assembly, implementing a contract defined in
`Relay.Providers.Abstractions`. Providers self-register through an
`IProviderModule` discovered at startup by convention. Host projects reference
provider assemblies by **wildcard**, not by name.

Adding a provider is: create `src/Relay.Providers.<Channel>.<Name>/`, implement
the contract, done. No existing file is edited — not the router, not the DI
registration, not the host `.csproj`.

This is asserted, not asserted-to: the fifth provider was added in its own commit,
and the diff for that commit touches only new files. `git show` on it is the
proof.

## Alternatives considered

**One assembly with a `switch` on provider type.** Rejected: it is the thing this
design exists to prevent. Every provider becomes an edit to a shared file, and
that file becomes the place where provider-specific concerns accumulate.

**Runtime plugin loading from a directory** (`AssemblyLoadContext` over a
`plugins/` folder). Genuinely tempting, and it would allow adding a provider
without recompiling. Rejected because it trades compile-time contract checking
for runtime failure, and buys deployment flexibility this project does not need.
Worth revisiting only if providers ever ship on a different cadence than the host.

**Configuration-driven generic HTTP provider.** Rejected: it works for the 80%
of providers that are a POST with a JSON body, and collapses for the rest — which
are the ones that matter. The abstraction would end up being a scripting language.

## Consequences

The provider count drives the project count; sixteen projects for a service this
size looks heavy, and that criticism is fair on a smaller system. The payoff is
that the contract is enforced by the compiler rather than by convention.

A wildcard `ProjectReference` is unusual and will surprise a reader. It is
commented at both call sites, because the surprise is mechanical rather than
architectural.

Every provider must pass the same contract test suite. If a provider cannot,
that is treated as evidence the abstraction is wrong — not as a reason to add an
opt-out flag to the interface.
