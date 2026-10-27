# ADR 0001 — Notification worker: transport, templating, delivery and idempotency

**Status:** Accepted · **Date:** 2026-10-23 · **Scope:** `src/ClaimPortal.Notifications`

Context: the API must return `201 Created` without ever touching an SMTP socket. Email
generation and delivery live in this separate process, which reacts to `ClaimSubmitted`
(see `docs/CONTRACT.md` §2). Each decision below records the alternative that was rejected
and why.

---

## 1. Email generation lives here, not in the API

A slow or dead mail relay must not be able to make claim submission slow or fail. With the
work behind a broker, an SMTP outage shows up as a message retrying, not as a 500 for a
policyholder who has just had a flood.

**Rejected:** sending from the API request handler, or from a `BackgroundService` inside the
API. Both put mail delivery in the API's failure domain, and the in-process background
variant additionally loses queued emails on every deploy, because nothing durable is holding
them.

## 2. Three seams: render → `IEmailSender` → idempotency store

`IClaimConfirmationRenderer` turns an event into an `EmailMessage` and does no I/O.
`IEmailSender` puts it on the wire. `INotificationDeliveryStore` decides whether to send at
all. The consumer is just the ordering of those three calls.

The payoff is concrete: moving production to SendGrid or Azure Communication Services is one
line in `NotificationsServiceCollectionExtensions` plus a new `IEmailSender` — no consumer,
template, idempotency or test change, because nothing above that interface knows SMTP exists.
And the renderer is unit-tested with no SMTP server, broker or database at all.

**Rejected:** one `ClaimSubmittedConsumer` that builds a `MimeMessage` inline. Shorter, but
the email content could then only be tested by standing up a mail catcher.

## 3. Scriban for templating

Single BSD-2-Clause assembly, no runtime compilation step, and the templates are authorable by
someone who is not a C# developer — which is the usual reason to want a mail template engine.
Templates are embedded resources, so the container image needs no content layout, and they are
parsed once in the constructor of a singleton: a template syntax error takes the worker down at
startup with Scriban's error list attached, rather than dead-lettering the first real claim.

**Rejected:** Razor via RazorLight. It would give compile-time checking of the model, but drags
the Razor compiler and a runtime-compilation story into a worker image for three templates.

All customer-supplied text is interpolated through `html.escape` in the HTML part — names
arrive from a public form. `ClaimConfirmationRendererTests` asserts the escaping, so deleting a
filter fails the build.

## 4. MailKit, a fresh `SmtpClient` per send, errors classified transient vs permanent

MailKit's client is not thread-safe and holds a stateful session; pooling it would mean
synchronising access across concurrent consumers to save a connection setup that costs
single-digit milliseconds against a local relay. At the volume where pooling pays, this class
is replaced by an HTTP provider behind `IEmailSender` anyway.

`SmtpEmailSender` translates provider failures into `TransientEmailDeliveryException` or
`PermanentEmailDeliveryException`, and the retry policy keys off that split. An SMTP 5xx, bad
credentials or a malformed recipient will be refused identically on every attempt, so retrying
only delays the message reaching a human on the error queue. Unknown failures are classified
**transient** on purpose: a retry costs one backoff interval, whereas misclassifying a blip as
permanent loses the customer's email.

## 5. Resilience: retry inside redelivery, then `_error`

Configured in `ClaimSubmittedConsumerDefinition`, not at the `UsingRabbitMq` call site, because
`ConfigureEndpoints` applies a definition on **every** transport — so RabbitMQ, Azure Service
Bus and the in-memory test harness all get the same policy. A policy written at the transport
call site is a policy the tests never exercise.

Filters run outside-in, so `UseDelayedRedelivery` is registered **before** `UseMessageRetry`:
retry a few times over seconds, then come back in 5 / 15 / 60 minutes, then dead-letter. The
other order would restart the in-process retries on every redelivery and make the effective
attempt count the product of the two.

MassTransit's per-endpoint `_error` queue is the dead-letter destination — not a queue we
invent. `ReturnsTheMessageToTheBrokerOnADelayOnceTheInProcessRetriesAreSpent` and
`FaultsAndDeadLettersWhenEveryRetryIsExhausted` prove the full chain by reading the real
`_error` queue, not by inferring from a `Fault<T>` event.

**Deployment note:** transport-level delayed delivery is native on the in-memory and Azure
Service Bus transports, but RabbitMQ needs the `rabbitmq_delayed_message_exchange` plugin. The
compose task must therefore build the broker image rather than pull it. `RedeliveryIntervals`
can be set empty to fall back to retry + `_error` alone on a broker without it.

## 6. Idempotency: `MessageId` as the key, in `notification_deliveries`

Per contract §2 the transport `MessageId` is the outbox row id and is stable across
redeliveries, so it is the key. It is the table's **primary key**, which makes the uniqueness
check the database's job, enforced under concurrency.

The protocol is claim → send → confirm:

1. `INSERT` a `Pending` row. Exactly one concurrent attempt can win; losers get a unique
   violation. This is the atomic compare-and-set — there is no `SELECT`-then-`INSERT` window.
2. The winner sends the email.
3. The winner marks the row `Sent` **after** the provider accepted it and **before** the
   message is acked.

A crash in that last window redelivers the message, which reads `Sent` and sends nothing.

**Rejected:** marking `Sent` before sending and committing once. An SMTP failure would then
leave a row claiming the customer was emailed when they were not, losing the confirmation
permanently with no trace. A duplicate confirmation is an annoyance; a silently missing one is
the failure that matters for an insurance claim, so the ordering never loses an email.

**Rejected:** deriving the key from the payload (hashing claim id + recipient). The contract
explicitly forbids it, and it over-deduplicates: a legitimate re-publish through a new outbox
row would be silently swallowed.
`TwoDistinctMessageIdsForTheSameClaimEachSendAnEmail` guards against that.

**Rejected:** MassTransit's `InMemoryOutbox` deduplication. It dedupes within a single bus
instance and does not survive a restart. A plain table is less machinery and an operator can
answer "did this customer get their email, and if not why not?" from SQL.

**Stale-claim recovery.** A worker killed mid-attempt would leave a row `Pending` forever and
the customer would never be emailed. After `Idempotency:StaleClaimAfter` (default 5 minutes) a
redelivery may take the claim. This is the one setting that can cause a duplicate, so it is
deliberately an order of magnitude longer than the 30-second SMTP timeout plus the retry
budget. The takeover is a conditional update pinned to the exact row version that was read, so
two redeliveries that both see the same stale row cannot both proceed.

**Residual risk, stated plainly.** A process killed between "SMTP accepted" and the
`MarkSentAsync` commit — a single database write — will send the email twice after the stale
window. Closing that completely requires the mail provider to deduplicate on a key we supply
(SendGrid and ACS both can), which is a change behind `IEmailSender`.

A unique violation is confirmed by re-reading the row rather than by matching a provider error
code. That keeps the store provider-agnostic — the tests run the real constraint on SQLite —
and, more importantly, stops a missing table or a dead connection from being misread as a
duplicate, which would silently suppress the email instead of failing loudly.

## 7. Separate `NotificationsDbContext`, own schema

The worker owns the `notifications` schema with its own migrations history table, and cannot
read or migrate the claims tables. It is a different deployable with a different owner, and the
only thing it knows about a claim is what the event told it. Sharing the API's context would
make "the consumer never reads the claims table" a promise rather than a structural fact.

Migrations are applied by a hosted service at startup so `docker compose up` on a clean volume
needs no manual step; `Database:MigrateOnStartup` turns that off for a real deployment, where
two replicas racing to migrate is a known way to deadlock a rollout.

**Rejected:** `EnsureCreatedAsync`. Shorter, but it cannot evolve a schema, so the first change
to this table would need migrations introduced under pressure.

## 8. Transport selected by configuration

`Messaging:Transport` picks `RabbitMq`, `InMemory` or `AzureServiceBus`. Both the API and the
worker read the same value: RabbitMQ locally, Service Bus in Azure, `InMemory` in tests. No
endpoint, host or credential is hard-coded anywhere in the assembly.

The Service Bus arm chooses the host from `Messaging:AzureServiceBus:ConnectionString` or
`Messaging:AzureServiceBus:FullyQualifiedNamespace` plus `DefaultAzureCredential`. The connection
string is a secret supplied by name, never committed. If the transport is selected with neither
setting present, startup fails loudly rather than falling back to RabbitMQ, which would look like
a working deployment while consuming nothing.

Only registration changes. The consumer, its definition (endpoint name, retry, redelivery), the
outbox and the idempotency store are transport-neutral; see `docs/TRANSPORT.md` for the full
switch, what it does and does not abstract, and the limits of what has been verified.

## 9. `InvariantGlobalization` is off for this project only

The repo-wide default in `Directory.Build.props` is `true`, and is right for the API and the
domain. This worker's entire output is customer-facing prose: it formats the incident date as
`October 24, 2026` in en-CA and converts the submission timestamp into `America/Winnipeg`.
Invariant mode cannot resolve an en-CA culture or an IANA time zone id, so the confirmation
email would silently degrade to UTC timestamps. The cost is that the runtime image must carry
ICU and tzdata; that is a Dockerfile line.

The meridiem (`a.m.`/`p.m.`) is written out in C# rather than taken from the culture's
`AMDesignator`, because recent CLDR data separates it with a narrow no-break space (U+202F)
that survives neither quoted-printable encoding nor a support agent copying the timestamp into
a claims system. The wording of a customer-facing email should not change because a base image
shipped new ICU data. A missing tz database still degrades to UTC with a warning rather than
failing the send.

## 10. Testing

- **Shouldly, not FluentAssertions** — FluentAssertions v8 is commercially licensed.
- **MassTransit pinned to 8.5.12** (Apache-2.0). 9.x moved to a commercial licence.
- **SQLite, not EF Core's InMemory provider**, for the idempotency tests. InMemory does not
  enforce unique constraints, and the constraint *is* the mechanism under test — those tests
  would pass against InMemory with the primary key removed from the model.
- The harness registers the **real** renderer, the **real** store and the **real** consumer
  definition. Only the transport and SMTP are substituted.
- `RecordingDeliveryStore` exists because the harness indexes its recorded message lists by
  `MessageId`, so a redelivery — which by contract carries the *same* id — collapses into a
  single entry and cannot be counted. Asserting on the claim outcome is the better test anyway:
  the redelivery test asserts the second attempt was told `AlreadySent`, rather than inferring
  deduplication from a send that did not happen.
- `global.json` opts `dotnet test` into Microsoft.Testing.Platform, which xunit.v3 requires on
  the .NET 10 SDK.
