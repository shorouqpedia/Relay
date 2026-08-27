# 1. Record architecture decisions

Status: Accepted

## Context

I have a strong habit of writing the reasoning behind a decision directly above
the code it affects — often at length. In systems that get inherited and
maintained for years, that habit pays for itself: the next person doesn't delete
a strange-looking line because the line tells them what breaks if they do.

But the habit has a failure mode, and this project is exactly the shape that
triggers it. On a greenfield codebase with no inherited constraints, a
forty-line comment block explaining an architectural choice reads as noise. The
reasoning is real; the placement is wrong. It sits in front of every reader
whether or not they are asking the question, and it drifts out of date without
anything failing.

## Decision

Architectural reasoning goes in `docs/adr/`. Inline comments explain mechanics,
not decisions, and are kept short.

The test for where something belongs: if a reader could *only* discover this by
being told, and it concerns a choice between viable options, it is an ADR. If it
concerns what a specific line physically does, it is a comment.

## Alternatives considered

**Keep the reasoning inline.** Rejected for the reasons above — but it is worth
saying that this is a calibration, not a reversal. The instinct to record why is
correct; only the location changes.

**A single ARCHITECTURE.md.** Rejected because one growing document has no way to
express supersession. A decision that gets reversed leaves no trace, so the
reader can't tell a considered choice from an accident.

**Nothing — let the code speak.** Rejected. Code states what was chosen and is
silent on what was rejected, which is the half that actually helps the next
person.

## Consequences

Reading the code alone will not explain the design; `docs/adr/` is part of the
source, not documentation about it.

Records go stale in a visible way rather than an invisible one: a superseded
record is still there, marked, next to the one that replaced it.

There is a real cost — writing these takes time that would otherwise go into
features, and on a portfolio project the ratio of docs to code is higher than it
would be on a product. That is accepted, because the reasoning *is* the artifact
being demonstrated here.
