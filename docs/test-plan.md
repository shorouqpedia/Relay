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

## Still to be written

Listed so the gaps are visible rather than merely absent.

| Prefix | Area | Milestone |
|---|---|---|
| PC | Provider contract suite — every provider assembly runs it | Provider contract |
| RT | Routing and provider selection under health and rate limits | Resilience |
| DC | Decorator chain composition and ordering | Resilience |
| OB | Outbox: atomic state change plus event, and dispatch | Persistence |
| IT | End-to-end HTTP against real infrastructure | Integration |
| CH | Chaos: providers that time out, rate-limit, and go silent | Integration |
