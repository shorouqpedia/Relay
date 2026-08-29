# Test plan

Every test method carries the id of the scenario it covers:
`<ScenarioId>_<Condition>_<ExpectedOutcome>`.

The point is what happens when a build goes red. A name like
`ML08_RecordAttempt_RetryableOutcome_DeadLettersWhenBudgetSpent` tells someone who
did not write the test which numbered behaviour broke, and this file tells them
why that behaviour was specified. Without the id, a failing test name is only a
description of some code.

It also makes the inverse question answerable: a row here with no test is a gap
that is visible rather than one that has to be noticed.

## ML — Message lifecycle

`tests/Relay.Domain.UnitTests/Messaging/MessageLifecycleTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| ML01 | A valid submission | Starts `Pending`, no attempts, raises `MessageQueued` |
| ML02 | Submitted with a retry budget of zero | Rejected — a message nobody may attempt is not a message |
| ML03 | Dispatch begins from `Pending` | Moves to `Dispatching`, records provider and start time |
| ML04 | Dispatch begins while already dispatching | Rejected, and the original provider is kept |
| ML05 | Provider accepts | Moves to `Sent`, records the provider's message id, raises `MessageSent` |
| ML06 | Provider rejects outright | Moves to `Failed` immediately — retrying a refusal changes nothing |
| ML07 | Transient failure, rate limit, or timeout, with budget left | Returns to `Pending`, releases the provider |
| ML08 | Same, with the budget spent | Dead-lettered, raises `MessageDeadLettered` |
| ML09 | An attempt recorded when not dispatching | Rejected, and nothing is appended to the history |
| ML10 | An attempt recorded with no outcome | Rejected, and nothing is appended to the history |
| ML11 | Several attempts | Numbered from one, in order |
| ML12 | Cancelled while `Pending` | Moves to `Cancelled` |
| ML13 | Cancelled after dispatch began | Rejected — Relay cannot unsend what a provider already has |
| ML14 | A stuck dispatch released, budget left | Returns to `Pending`, charging a `Timeout` attempt |
| ML15 | A stuck dispatch released, no budget left | Dead-lettered |
| ML16 | Abandoned from `Sent` | Dead-lettered with the sweeper's reason |
| ML17 | Abandoned when already terminal | Rejected, state untouched |
| ML18 | Events drained | Returns what accumulated and empties the collection |

## DR — Delivery receipts

`tests/Relay.Domain.UnitTests/Messaging/DeliveryReceiptTests.cs`

These are the scenarios ADR 0008 exists for. They are also the ones a naive
implementation gets backwards, because the intuitive answer is wrong twice.

| Id | Scenario | Expected |
|---|---|---|
| DR01 | Delivery receipt for a sent message | Moves to `Delivered`, raises `MessageDelivered` |
| DR02 | The same receipt twice | Succeeds, changes nothing, raises one event |
| DR03 | Receipt before any provider accepted | Rejected |
| DR04 | Receipt arriving after the sweeper gave up | Rejected; the message stays dead-lettered |
| DR05 | Negative receipt for a sent message | Moves to `Failed` with the reason |
| DR06 | The same negative receipt twice | Succeeds; completion time does not move |
| DR07 | Negative receipt after delivery | Rejected |
| DR08 | A provider that returned a message id | The id is recorded, so a receipt can be matched back |

**Why DR02 succeeds rather than conflicting.** A provider that receives an error
treats the receipt as undelivered and sends it again. Answering "already done" is
what actually stops the retries; answering "conflict" causes them.

**Why DR04 is rejected even though the receipt is true.** The message really was
delivered and the sweeper was wrong to give up — but something has already
reported this message as dead-lettered, and silently reversing that would make
the earlier report a lie. The receipt is recorded as an audit fact instead.

## VO — Value objects

`tests/Relay.Domain.UnitTests/Messaging/ValueObjectTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| VO01 | Addresses valid for their channel | Accepted |
| VO02 | Addresses invalid for their channel | Rejected |
| VO03 | A well-formed phone number on the email channel | Rejected — validity is per channel, not absolute |
| VO04 | Email address differing only in case | Normalised, and equal |
| VO05 | Blank address | Rejected as missing |
| VO06 | The unset channel | Rejected |
| VO07 | Idempotency key with padding and mixed case | Normalised, equal, same hash code |
| VO08 | Key absent, blank, or too short | Rejected |
| VO09 | Key longer than the unique index | Rejected |
| VO10 | Empty body | Rejected |
| VO11 | A body too long for SMS | Rejected on SMS, accepted on email |
| VO12 | Subject on email or push | Accepted, trimmed |
| VO13 | Subject on SMS or webhook | Rejected |
| VO14 | Blank subject on any channel | Treated as absent |
| VO15 | Well-formed provider slugs | Accepted |
| VO16 | Mis-cased or malformed provider slugs | Rejected, not normalised |

**Why VO07 and VO16 disagree about case.** An idempotency key is echoed back by a
caller who may not reproduce it exactly, and two spellings genuinely mean one key
— so it normalises. A provider id is written by hand into configuration and must
match a registered provider, so accepting `Email.Postal` for `email.postal` would
make a mistyped routing rule look like it worked. The rule is the same in both
cases: normalise when two spellings mean one thing, reject when they do not.

## PC — Provider contract

`tests/Relay.Providers.ContractTests/ProviderContract.cs`

One abstract suite, run once per provider assembly. A provider supplies an
instance and a controllable upstream and adds no test methods of its own.

| Id | Scenario | Expected |
|---|---|---|
| PC01 | The descriptor | Names a channel, an id, and a receipt window |
| PC02 | Upstream accepts | `Accepted`, no failure reason |
| PC03 | Accepted, and `ReturnsMessageId` declared | A message id comes back |
| PC04 | Upstream refuses permanently | `Rejected`, not a transient failure |
| PC05 | Upstream unavailable | `TransientFailure` |
| PC06 | Upstream rate limits | `RateLimited`, however it expressed it |
| PC07 | Rate limited, and `ReportsRetryAfter` declared | A retry delay comes back |
| PC08 | Upstream never responds | `Timeout` — **not** `TransientFailure` |
| PC09 | Upstream returns an unparseable body | A result, not an exception |
| PC10 | Caller cancels | `OperationCanceledException` propagates |
| PC11 | Upstream echoes the API key in an error | The credential is masked |
| PC12 | Receipt-query capability | Flag and implementation agree, both ways |

**Why PC08 is separate from PC05.** A transient failure asserts the message was
not sent. A timeout means it may have been. Only the second makes a retry a
decision to risk a duplicate, and conflating them produces a system that is
confidently wrong about what it did. This scenario caught exactly that
misclassification in the first provider.

**Why PC11 exists.** Upstreams echo the submitted request back in error payloads
routinely, and a provider that copies the message into `FailureReason` has put a
credential into the log store.

## PR — Persistence

`tests/Relay.IntegrationTests/Persistence/MessagePersistenceTests.cs`

Not tests of the mapping code. Tests of the properties the design delegates to
the storage engine — which is why none of them could be written against an
in-memory provider ([ADR 0010](adr/0010-testcontainers-over-in-memory.md)).

| Id | Scenario | Expected |
|---|---|---|
| PR01 | A message round-trips | Value objects come back as value objects, still normalised |
| PR02 | Two messages with one idempotency key | The second is refused by the unique index |
| PR03 | A retry spelling the key differently | Finds the original |
| PR04 | Two workers dispatch the same message | The second loses on the concurrency token |
| PR05 | Several attempts | Persist in sequence, inside the aggregate |
| PR06 | A receipt arrives with a provider message id | Finds its message |
| PR07 | Two workers claim concurrently | Disjoint sets, neither blocked |
| PR08 | A backlog is claimed | Oldest first |

**Why PR04 and PR07 are both needed.** They protect different windows. The row
lock in PR07 stops two workers taking the same row; the `xmin` token in PR04
stops a worker that was paused past the point where its lock mattered. Either
alone leaves a path to a duplicate send.

## OB — Outbox

`tests/Relay.IntegrationTests/Persistence/OutboxTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| OB01 | An aggregate is saved | Its events are written in the same transaction |
| OB02 | The same aggregate is saved twice | The event is written once |
| OB03 | The dispatcher runs | Pending rows publish and are marked processed |
| OB04 | Publishing fails | The row stays pending, with the reason recorded |
| OB05 | One row in a batch fails | The rest still publish |
| OB06 | Two dispatchers run concurrently | Neither publishes the other's rows |

## Still to be written

Listed so the gaps are visible rather than merely absent.

| Prefix | Area | Milestone |
|---|---|---|
| RT | Routing and provider selection under health and rate limits | Resilience |
| DC | Decorator chain composition and ordering | Resilience |
| IT | End-to-end HTTP through the API | Integration |
| CH | Chaos: providers that time out, rate-limit, and go silent | Integration |

## RT — Routing

`tests/Relay.Application.UnitTests/Delivery/ProviderRouterTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| RT01 | Several providers on one channel | The lowest priority number wins |
| RT02 | Providers on other channels | Ignored |
| RT03 | No provider serves the channel | Fails — a configuration fault, not an outage |
| RT04 | The preferred provider is circuit-broken | The next one is chosen |
| RT05 | The message already failed on the preferred provider | A different provider is chosen |
| RT06 | Every provider has been tried | Falls back to one already tried |
| RT07 | Every provider is unavailable | Fails; the message stays pending |
| RT08 | An untried provider and a healthier tried one | Untriedness beats priority |

**Why RT05 and RT06 are both here.** They look contradictory and are not. RT05 is
the rule: a retry that goes back to the provider that just failed spends the retry
budget on one upstream while a working alternative sits idle. RT06 is what happens
once there is no alternative left — retrying a possibly-transient failure beats
refusing to send at all. The order matters: exhausting the alternatives is what
unlocks the fallback, not preferring it.

## DP — Dispatch

`tests/Relay.Application.UnitTests/Delivery/MessageDispatcherTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| DP01 | The provider accepts | The message is Sent, with the provider's id recorded |
| DP02 | Any dispatch | The claim is committed **before** the provider is called |
| DP03 | The provider rejects | Failed, terminally |
| DP04 | Transient failure with budget left | Back to Pending, provider released |
| DP05 | Any outcome | Recorded against provider health |
| DP06 | No provider available | Nothing is written and nothing is attempted |
| DP07 | The message was cancelled after being claimed | The provider is not called |

**DP02 is the one worth reading.** It asserts an ordering, not a value. A process
that dies during the provider call has to leave a row in `Dispatching` for the
recovery loop to find. If the claim were committed afterwards, the message would
still read as `Pending` despite possibly having been sent, and the next worker
would send it again with nothing recording that it had been.

## DC — Decorator chain

`tests/Relay.Providers.ContractTests/DecoratorChainTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| DC01 | Resolving `IMessageProvider` | Yields a decorated instance, never a bare provider |
| DC02 | The composed chain | Logging → Metrics → RateLimit → Resilience → provider |
| DC03 | The chain's descriptor | Forwarded unchanged from the real provider |

**Why DC02 exists.** The ordering in ADR 0007 is load-bearing and invisible at
every call site. Moving the rate limiter below the resilience decorator lets
retries bypass the quota — so a provider refusing because it is overloaded gets
hit harder for refusing — and nothing else in the system would notice.

These tests also caught a real defect on first run: `AddRefitClient` resolves
through a reflection-based builder that Refit 15 no longer ships by default, so
the provider registered, built cleanly, and threw `NotSupportedException` on its
first resolve. Nothing before this point exercised the real container.

## IT — HTTP surface, end to end

`tests/Relay.IntegrationTests/Api/MessageEndpointTests.cs`

Real requests through the real host against a real database. Nothing is
substituted, so these cover what only exists once the pieces are assembled: the
validation filter actually running, error mapping producing the status a client
branches on, and idempotency surviving an identical second request.

| Id | Scenario | Expected |
|---|---|---|
| IT01 | A valid submission | 201, with the id and a Location header |
| IT02 | The same key submitted twice | 201 then **200**, same message id |
| IT03 | The key supplied as a header | Deduplicates the same way |
| IT04 | No key at all, identical requests | Deduplicates on the derived key |
| IT05 | A recipient invalid for its channel | 400 with `message.recipient_invalid_for_channel` |
| IT06 | An empty body | 400 with per-field errors from the filter |
| IT07 | A subject on SMS | 400 |
| IT08 | Reading a submitted message | 200 with an empty attempt history |
| IT09 | Reading a message that does not exist | 404, `application/problem+json` |
| IT10 | Cancelling while pending | 204, and the message reads `Cancelled` |
| IT11 | Cancelling twice | 204 again |
| IT12 | Cancelling a message that does not exist | 404 |

**Why IT02 expects 200 rather than 409.** A caller retrying after a timeout wants
the outcome of their message. An error makes them handle a failure for something
that worked, and the status is the only thing distinguishing the retry that
landed from the one that did not.

**What this suite caught on first run.** Every submission returned a bare 400
with no detail. `ChannelType` was arriving as `"Email"` and System.Text.Json
binds enums from numbers by default, so the body failed to deserialise before any
code ran. Nothing below the HTTP boundary could have found it — the handler tests
construct the command directly.

It also caught an empty connection string passing a null check: `appsettings.json`
ships the key with an empty value so CI can assert no secret was committed, which
means the absent case in practice is `""` and not `null`. The failure surfaced far
away, inside the Npgsql driver.

## PC09 and PC09a — a contract assumption, found by a fifth provider

`PC09` originally asserted that a provider meeting an unparseable response
reports `TransientFailure`. Four providers passed it. The fifth did not, and it
was right not to.

The webhook provider posts to an endpoint the recipient supplied. It never reads
the response body — the status line is the entire answer — so a 200 carrying
malformed content is a genuine success, and reporting a failure would have been
the bug. The assertion had quietly assumed every provider parses a body to decide
whether it succeeded.

So the shared scenario now states only what is true of all of them: an unexpected
response must not throw. The sharper statement moved to `PC09a` in the four
subclasses where it holds, phrased as what it actually is — a property of
providers that read a body.

This is [ADR 0003](adr/0003-provider-plugin-architecture.md) working as written:
when a provider cannot pass the suite, the first suspect is the abstraction. The
alternative — adding an opt-out flag so the webhook provider could skip `PC09` —
would have preserved a false claim and made the contract a little less shared
every time it happened.

## WH — Webhook specifics

`tests/Relay.Providers.ContractTests/Providers/WebhookContractTests.cs`

Not part of the shared contract, because no other provider signs anything.

| Id | Scenario | Expected |
|---|---|---|
| WH01 | Any send | Signed HMAC-SHA256 over `timestamp.payload` |
| WH02 | Any send | Carries the message id in a header |
| WH03 | Any send | Posts to the recipient's own address |

**Why the timestamp is inside the signature.** Signing the payload alone produces
a signature that stays valid forever, so anyone who observed one request could
replay it indefinitely and every copy would verify. Receivers are expected to
reject a timestamp outside a few minutes.

**Why WH02 exists.** Relay delivers at least once, so the same webhook can
legitimately arrive twice. The receiver needs a key to deduplicate on, in a header
so it can do so without parsing the body.

## CS — Callback signatures

`tests/Relay.Providers.ContractTests/CallbackSignatureTests.cs`

Two short functions every verifier depends on. Both are easy to write in a way
that looks correct, passes the obvious tests, and can be defeated.

| Id | Scenario | Expected |
|---|---|---|
| CS01 | A signature over the same payload | Accepted |
| CS02 | A signature made with a different secret | Rejected |
| CS03 | A real signature against an altered payload | Rejected |
| CS04 | Null on either side | Rejected |
| CS05 | Differences at the start and at the end of a signature | Compared in the same time |
| CS06 | A recent timestamp | Accepted |
| CS07 | A timestamp from two hours ago | Rejected |
| CS08 | A clock slightly ahead, and one far ahead | Accepted, then rejected |
| CS09 | A timestamp that is absent or unparseable | Rejected |

**CS05 is the one worth reading.** String equality returns as soon as it finds a
differing character, so how long a rejection takes reveals how many leading
characters were right — and an attacker who can measure that recovers a valid
signature one character at a time, turning a 256-bit search into a few hundred
requests. The bound is deliberately loose, because CI timing is noisy and what
this can actually detect is the difference between constant time and a comparison
that walks the string: an order of magnitude, not a few percent.

**CS08 checks both directions.** A provider whose clock runs slightly fast sends
timestamps in the future, and rejecting those would fail legitimate traffic for a
reason nobody would think to look for.

## CB — Callback endpoint

`tests/Relay.IntegrationTests/Api/CallbackEndpointTests.cs`

The only public write path in the system, so these are as much security tests as
functional ones.

| Id | Scenario | Expected |
|---|---|---|
| CB01 | A valid signature, no matching message | 204 |
| CB02 | A delivery receipt for a sent message | 204, message reads `Delivered` |
| CB03 | A bounce | `Failed`, with the provider's reason |
| CB04 | The same callback twice | 204 both times |
| CB05 | A forged signature | 401, message unchanged |
| CB06 | Signed with the wrong secret | 401 |
| CB07 | A real signature over an altered body | 401, message unchanged |
| CB08 | A correctly signed callback from two hours ago | 401, message unchanged |
| CB09 | No signature headers at all | 401 |
| CB10 | Correctly signed, unreadable payload | **422**, not 401 |
| CB11 | A provider not registered here | 404 |
| CB12 | Any callback, including refused ones | Recorded, with the reason |

**Why CB01 and CB04 expect 204.** The status answers "did you receive this?", not
"did it do anything?". A provider reading a 4xx treats the receipt as undelivered
and resends — so returning a conflict for a duplicate causes the retries it
appears to be reporting.

**Why CB10 is 422 rather than 401.** A correctly signed payload Relay cannot read
means the provider changed its format. That is a bug to fix, and a blanket 401
would bury it among the internet background noise any public endpoint attracts.

**What CB01 caught on first run.** Every callback returned 404, because provider
discovery had been reading `Assembly.GetEntryAssembly().GetReferencedAssemblies()`
— and under a test runner the entry assembly is the runner, not the host. No
provider assemblies were ever loaded. The failure was silent by construction:
discovery finding nothing looks exactly like there being nothing to find, so the
API had been running with zero providers and every earlier test had passed anyway
because none of them needed one. Discovery now scans the deployment directory.

## TR — Trace linking

`tests/Relay.Application.UnitTests/Observability/TraceLinkTests.cs`

| Id | Scenario | Expected |
|---|---|---|
| TR01 | No submission context recorded | A span, with no link |
| TR02 | An unparseable context | A span, with no link |
| TR03 | A valid submission context | Linked to that trace |
| TR04 | A valid submission context | A **new** trace id, no parent span |
| TR05 | Another activity is current | Still a new trace, still linked |

**TR04 is the assertion the design rests on.** Sharing a trace id with the
submission would leave the backend holding one trace open from submission until
the last delivery receipt — possibly hours — and computing latency percentiles
over it.

**TR05 caught a real defect on its first run.** `StartActivity` with
`parentContext: default` does not mean "no parent": .NET reads it as "no parent
was specified" and falls back to `Activity.Current`. A dispatch running inside any
other span therefore continued that span's trace, silently. Nothing in the worker
is ambient today, so it would have surfaced the first time dispatch ran inside a
request — in one hosting shape and not the other, which is the worst way to find
a bug. `Activity.Current` is now cleared for the duration of the start call.
