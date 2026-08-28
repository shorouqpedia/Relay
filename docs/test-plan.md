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
