# 9. No catch-all `try/catch`, no `#region`

Status: Accepted

## Context

Two habits from my prior work are deliberately not carried into this codebase.
Both are recorded here rather than quietly dropped, because a reviewer comparing
this repository to my other work should see that the difference is a decision.

**`try/catch` wrapping an entire handler body.** The intent is defensive: nothing
escapes, every failure is logged. The effect is that the handler's contract
becomes unreadable. A caller cannot tell whether a failure will arrive as a
returned error or a thrown exception, because the handler produces both and
converts between them privately. It also duplicates the global exception handler,
so the same failure gets logged twice at different levels of detail.

**`#region` inside a method body.** A region labelled "validation" or "build the
request" is a correct observation that the method has phases — and phases with
names are methods with names. The region collapses the symptom in the editor
while leaving the method long.

## Decision

Neither appears in this codebase.

- Expected failures are `Result` values (ADR 0005). Unexpected ones reach the
  global exception handler. A handler that catches is catching one specific
  exception type, for one specific reason, with a comment saying why.
- `#region` is not used at all. Where a method has phases, the phases become
  private methods.

Both are checked in CI by a script over the source tree, not left to review.

## Alternatives considered

**Allow `try/catch` with a rethrow for logging context.** Rejected: structured
logging with a scope achieves the same enrichment without changing the control
flow, and without the risk of the rethrow losing the original stack trace.

**Allow `#region` at the type level** to group members. Rejected on a smaller
margin — it is much less harmful than the in-method form. Disallowed outright
because a partial exception invites the full habit back, and because a type
needing regions to be navigable is usually a type doing several jobs.

## Consequences

Some handlers will let an exception escape that a catch-all would have converted
into a friendly error. That is intended: it means an unanticipated failure is
visible as a 500 and an alert, rather than being flattened into a generic failure
response and never investigated.

The CI check is a blunt instrument — it will occasionally flag a legitimate
construct. When it does, the fix is a narrow suppression with a justification,
which is a fine place for a comment, since it explains a mechanism rather than a
decision.
