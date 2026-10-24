# Architecture overview

A home-insurance claim submission slice. The requirement that shapes every boundary is the
assignment's own: **the API persists and publishes; a separate process renders and sends the
email.** The API does not reference a mail library, and a test asserts it.

## Components

```
web/ (React 19 + Vite)          Browser form, Zod validation, confirmation screen
        |  POST /api/claims
        v
ClaimPortal.Api                 Validates, mints CLM-YYYYMMDD-XXXXXX, writes claim + outbox
        |                       row in one transaction, publishes ClaimSubmitted
        v
PostgreSQL                      public.claims, public.outbox_message,
        |                       notifications.notification_deliveries
        v  (outbox dispatcher -> broker)
RabbitMQ (local) /              ClaimSubmitted
Azure Service Bus (cloud)
        |
        v
ClaimPortal.Notifications       MassTransit consumer -> Scriban render -> MailKit SMTP
        |
        v
Mailpit (local) / SMTP relay    The reviewer reads the real email in a browser
```

| Project | Owns | Deliberately does not own |
| --- | --- | --- |
| `ClaimPortal.Contracts` | `SubmitClaimRequest`, `SubmitClaimResponse`, `ClaimSubmitted`, the loss-type enum. The only thing both services share. | Behaviour. No dependencies beyond the BCL. |
| `ClaimPortal.Domain` | Claim aggregate, value objects (postal code, province, claim number), claim-number policy, repository port. | EF Core, ASP.NET, messaging. |
| `ClaimPortal.Infrastructure` | `ClaimsDbContext`, mappings, migrations, outbox tables, repository implementation. | HTTP or messaging concerns. |
| `ClaimPortal.Api` | `POST /api/claims`, FluentValidation rules, ProblemDetails mapping, outbox publish, health, Serilog, OTLP. | Email rendering or delivery. |
| `ClaimPortal.Notifications` | Consumer, retry/redelivery policy, idempotency store, templates, SMTP sender, health endpoint. | Reading the claims tables; it owns only its `notifications` schema. |
| `ClaimPortal.Observability` | Shared JSON logging, correlation-id context, OTLP tracing, Postgres/RabbitMQ health probes. | Domain logic. |
| `web/` | Form, client validation mirroring the server, confirmation screen, nginx same-origin proxy. | Any business rule the server does not also enforce. |

## Request sequence

1. The browser posts the form to `/api/claims` on the same origin; nginx proxies it to the API.
2. The API validates with FluentValidation and returns RFC 7807 `ProblemDetails` on failure,
   with camelCase/dotted keys that map onto the form fields (`propertyAddress.postalCode`).
3. On success it mints the claim number, creates the aggregate, and calls
   `IPublishEndpoint.Publish(new ClaimSubmitted(...))` **before** `AddAsync` + `SaveChanges`.
   With MassTransit's EF bus outbox, the publish becomes an `outbox_message` row, so the claim
   and its event commit in the same transaction.
4. The API returns `201` with `Location: /api/claims/{claimId}`. It has sent no email and has
   no way to send one.
5. The outbox dispatcher publishes committed rows to the broker.
6. The worker consumes `ClaimSubmitted`, claims a row in `notification_deliveries` keyed by the
   transport `MessageId`, renders HTML and plaintext with Scriban, and sends through
   `IEmailSender` (MailKit/SMTP).
7. The customer sees the claim number on the confirmation screen and in the email. They are the
   same string, and the email also carries it in the subject.

## Data ownership

Two schemas, two deployables:

- `public.claims` and `public.outbox_message` are owned by the API. `claims` has a unique index
  on the claim number; a collision is retried a bounded number of times rather than trusted to
  the generator.
- `notifications.notification_deliveries` is owned by the worker, with a unique key on the
  message id. The worker cannot read the claims tables.

The event carries everything the email needs (event-carried state transfer), so the worker
never calls back into the API. The loss description, estimate and property address are
deliberately excluded from the event: the email does not need them and they should not travel
through an email pipeline.

## Failure semantics

| Failure | Behaviour |
| --- | --- |
| Crash between "claim saved" and "event published" | The outbox row is in the same transaction, so either both exist or neither does. Nothing is lost. |
| Duplicate delivery of the same event | The unique `MessageId` row in `notification_deliveries` prevents a second email. `MessageId` is the stable outbox id, so it survives redelivery. |
| Transient SMTP failure | In-process exponential retry (5 attempts, 1s to 30s), then delayed redelivery at 5 minutes, 15 minutes and 1 hour. |
| Relay down for over ~90 minutes | The message dead-letters to the `_error` queue; the runbook is documented. |
| Unknown transport value | Explicit configuration error, not a silent fallback to RabbitMQ. |
| Dependency outage | `/health/ready` probes the database and broker and returns `503`; `/health/live` stays `200`. |

## Observability

Serilog writes compact JSON to stdout. The correlation id from `X-Correlation-ID` (or a
generated one) flows HTTP -> event header -> worker log, and the trace id is on every line.
OpenTelemetry exports OTLP to a local collector (compose) or Honeycomb (when
`HONEYCOMB_API_KEY` is set). Claimant email, phone and address are never logged; claim id,
claim number, correlation id and trace id are. A single claim produces one trace spanning
`claim-portal-api` and `claim-portal-worker`.

## Deployment shapes

- **Local / assessment.** `docker compose up --build` runs Postgres, RabbitMQ with the
  delayed-message plugin, Mailpit, the collector, Jaeger, the API, the worker and the web app.
  No cloud account, key or signup is required.
- **Azure (optional).** Container Apps runs the API, worker, web and Mailpit; PostgreSQL
  Flexible Server, Service Bus Standard, ACR and Log Analytics back them. Every app has
  `minReplicas: 0`; the worker scales on queue depth. See
  [`deploy/azure/README.md`](../deploy/azure/README.md).

The transport is configuration: `Messaging:Transport` selects RabbitMQ or Azure Service Bus
with no change to the consumer, the contract or the outbox. What that does and does not
abstract is in [`TRANSPORT.md`](TRANSPORT.md).

## Decisions and the alternatives that were rejected

| Decision | Alternative rejected | Why |
| --- | --- | --- |
| Transactional outbox | Publish after `SaveChanges` | The gap loses the email permanently on a crash. |
| Separate worker process | Send email inside the API | The assignment's pass/fail rule; also keeps SMTP latency and failure out of the request path. |
| PostgreSQL + EF Core migrations | In-memory database | A real database is barely more work and shows migration discipline. |
| MassTransit over RabbitMQ / Service Bus | Raw client + hand-written dispatcher | Retry, redelivery, `_error` and the test harness would all be rebuilt by hand. |
| Mailpit behind `IEmailSender` | A real provider account | Zero credentials, and the reviewer can see the delivered email. |
| Shouldly | FluentAssertions 8 | FluentAssertions changed to a paid licence; shipping it in a work sample is a licensing landmine. |
| Scriban templates | String interpolation | Rendering is unit-testable without SMTP, and HTML/text stay in step. |
| Event carries email data | Worker calls the API for details | Keeps the two services independent, at the cost of a slightly wider event. |

The worker-specific reasoning, including the exact idempotency protocol, is in
[`docs/adr/0001-notification-worker.md`](adr/0001-notification-worker.md).

## Non-goals

Authentication and authorisation, claim status updates, document upload, payments, a real mail
provider, and multi-region deployment. The brief asks for a polished vertical slice with
defensible decisions, not a production claims system; each of these would be a separate
component with its own failure modes.
