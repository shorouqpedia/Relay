# 16. CI verifies the claims this repository makes about itself

Status: Accepted

## Context

This repository asserts things about itself in prose. The README says adding a
provider edits no production code. ADR 0009 says `#region` and unfiltered
`catch (Exception)` do not appear. The configuration convention says
secret-bearing keys ship empty.

Every one of those was true when it was written. None of them stays true on its
own. Prose does not fail, so a claim that stops holding decays quietly into a
lie — and a repository whose documentation confidently describes something it no
longer does is worse than one with no documentation, because a reader has no way
to tell which parts are still accurate.

The usual answer is review. Review catches a `#region` if the reviewer knows the
rule and is looking; it does not catch a secret added to `appsettings.json` in a
forty-file diff, and it never catches the claim that was true in March.

## Decision

**Anything this repository claims about itself is checked in CI, or it is not
claimed.**

Concretely, the pipeline enforces:

- **The build gates.** `TreatWarningsAsErrors` and transitive NuGet auditing are
  on, so a newly published advisory against any package in the graph fails the
  build rather than sitting in a report nobody opens.
- **The style rules ADR 0009 states.** `build/check-style.sh` fails on `#region`
  and on an unfiltered `catch (Exception)`. Neither is expressible in
  `.editorconfig`, which is exactly why the ADR previously named a script that
  did not exist — a claim about enforcement that was itself unenforced.
- **The configuration convention.** `build/check-config.sh` fails when a
  secret-bearing key in a committed settings file has a value, or when a
  connection string carries a password.
- **The full test suite**, including the integration tests, against a real
  PostgreSQL. A pipeline that skips the tests needing infrastructure is a
  pipeline that does not run the tests that find things.

Findings go to GitHub's code-scanning tab as SARIF rather than to build logs.
A warning in a log is read once, by whoever happened to be watching; an alert in
code scanning is attached to the line, survives, and shows up on the pull request
that introduced it.

## Alternatives considered

**Rely on review.** Rejected as the only mechanism, for the reasons above. It
remains the mechanism for everything a script cannot express, which is most
things — these checks cover the few rules that are mechanical enough to automate
and important enough to be worth automating.

**Warn rather than fail.** Rejected. A warning that does not block is a warning
that accumulates: the count goes up, the threshold for "normal" moves with it,
and after a few months nobody can tell which warnings are new. Everything here
either blocks or is not checked.

**A pre-commit hook instead.** Rejected as the primary gate — hooks are per
machine, are skipped with `--no-verify`, and are absent for anyone who clones
without running a setup step. They are a fine convenience on top of a CI gate and
a poor substitute for one.

## Consequences

The scripts are shell, so they run identically in CI and locally, and a developer
can find out why the build is red without pushing. They are also blunt: a regular
expression over source will eventually flag something legitimate. The escape
hatch is a marker comment on the line — `// check-style: allow-broad-catch — why`
— which forces the exception to be written down next to what it excuses rather
than added to a list somewhere else.

Container image scanning will report vulnerabilities in the base image that no
action can fix until Microsoft publishes a new one. Those are reported and do not
fail the build, which is the one place this decision bends: a gate nobody can
pass teaches people to bypass gates.

CI now takes longer, because the integration tests start containers. That is the
cost of testing against the thing rather than a substitute for it, and it is the
same argument as ADR 0010.
