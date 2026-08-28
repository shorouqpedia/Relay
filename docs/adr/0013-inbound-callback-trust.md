# 13. Trusting an inbound callback

Status: Accepted

## Context

Providers report delivery by calling Relay back. That endpoint is the only part
of the system where an outside party changes a message's state, and it has
properties nothing else does:

- it is **public**, because a provider cannot authenticate the way a first-party
  client does
- it is **unauthenticated in the usual sense** — no bearer token, no session
- it **writes**, moving a message to `Delivered` or `Failed`
- its callers **retry aggressively**, because from their side an unacknowledged
  receipt looks lost

Anyone who can guess the URL can post to it. Without verification, a stranger
could mark any message delivered — which is worse than it sounds, because the
system would then stop reconciling it and report a lie with full confidence.

## Decision

**Four rules, and the failure of any one is a rejection.**

**1. The signature is verified over the raw bytes, in constant time.**

Each provider signs the callback with a shared secret. Verification happens on
the exact bytes received, before deserialization — re-serializing a parsed object
produces different bytes and a signature that never matches, so the body must be
read raw and buffered.

Comparison uses `CryptographicOperations.FixedTimeEquals`. A `==` on two byte
arrays returns as soon as it finds a difference, so how long a rejection takes
reveals how many leading bytes were right, and an attacker can recover a valid
signature one byte at a time. The correctness of the comparison is identical; only
the timing differs, which is exactly why this is easy to get wrong and invisible
in review.

**2. The signed payload includes a timestamp, and the timestamp must be recent.**

A signature over the body alone stays valid forever, so a single captured
callback can be replayed indefinitely and every copy verifies. Signing
`timestamp.body` and rejecting anything outside a few minutes bounds that window.

The window is a trade: too tight and a provider's own retry after a network delay
is rejected as an attack; too loose and a captured callback stays useful. Five
minutes is the usual industry choice and is what this uses.

**3. Every callback is recorded, including the ones that change nothing.**

A receipt for a message already delivered, a receipt for a message the sweeper
abandoned, a receipt with a signature that failed — all are written to a durable
log. That log is the only way to answer "did the provider tell us, and when?"
after an incident, and the interesting cases are precisely the ones that changed
no state.

**4. A well-formed callback is acknowledged with 200 even when it changed nothing.**

This is the rule that looks wrong and is not. The status code answers "did you
receive this?", not "did it do anything?". A provider that gets a 4xx treats the
receipt as undelivered and sends it again — so returning 409 for a duplicate
causes the retries it appears to be reporting.

4xx is reserved for callbacks Relay genuinely cannot accept: a bad signature, an
unparseable body, an unknown provider. Those are worth failing loudly, because
each means something is misconfigured rather than merely late.

## Alternatives considered

**A shared secret in the URL or a header, compared directly.** Rejected: it
travels in full on every request, appears in access logs and proxies, and a
constant-time comparison of the whole secret is still a comparison of a value
that never changes. A signature proves possession without transmitting it.

**Mutual TLS.** Genuinely stronger, and rejected on operational grounds: it
requires every provider to support client certificates and requires certificate
distribution and rotation for each. HMAC is what providers actually implement.

**Trusting the provider's IP range.** Rejected — allow-lists go stale silently,
providers change infrastructure without notice, and anything behind a proxy sees
the proxy's address anyway.

**Rejecting late receipts outright.** Rejected: a receipt arriving after the
sweeper gave up is true information, and discarding it destroys the only record
that the message actually arrived. It is recorded as an audit fact instead, and
the message stays in the terminal state something already reported (ADR 0008).

## Consequences

Every provider that pushes receipts needs a signing secret configured, and
rotating one breaks its callbacks until both sides are updated. There is no
grace period implemented — accepting either of two secrets during a rotation is
the standard answer and is a deliberate omission for now, recorded here so it is
a known gap rather than an oversight.

The raw body has to be buffered before it is parsed, which means the endpoint
holds the whole payload in memory. Bounded by a request size limit; the batching
providers send tens of receipts, not thousands.

A verification failure returns 401 with no detail. That is deliberately unhelpful:
telling a caller whether the signature was wrong, the timestamp stale, or the
provider unknown helps an attacker more than it helps a misconfigured provider,
who will find the answer in Relay's own logs.
