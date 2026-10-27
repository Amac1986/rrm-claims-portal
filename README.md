# Red River Mutual  Home Insurance Claim Portal

A vertical slice of a home-insurance claim submission portal: a React form posts a claim to
an ASP.NET Core API, the API persists it and writes a `ClaimSubmitted` event through a
transactional outbox, a **separate** notification worker consumes that event and emails the
customer a confirmation containing the claim number.

```
Browser (React)
      |
      | POST /api/claims
      v
Claims API --- one transaction ---> PostgreSQL
      |                               claims + outbox_message
      | (outbox dispatcher)
      v
 RabbitMQ --- ClaimSubmitted ---> Notification worker --- SMTP ---> Mailpit (local)
                                        |
                                        v
                                  notifications.notification_deliveries (idempotency)
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the component responsibilities, the
request sequence, data ownership, failure semantics and the rejected alternatives.

## Quick start

Prerequisites: Docker Desktop (or another Docker engine) with Compose v2.

```bash
docker compose up --build
```

Then:

| What | Where |
| --- | --- |
| Claim form | http://localhost:5173 |
| Mailpit (the sent email) | http://localhost:8025 |
| API health | http://localhost:8080/health/live |
| API readiness | http://localhost:8080/health/ready |
| Worker health | http://localhost:8081/health/live |
| Worker readiness | http://localhost:8081/health/ready |
| Local traces (Jaeger) | http://localhost:16686 |
| RabbitMQ management (`guest` / `guest`) | http://localhost:15672 |

Submit the form, then open Mailpit. The confirmation email contains the same claim number the
confirmation screen displayed. `docker compose down -v` followed by `docker compose up` works
again from a clean database with no manual migration step.

## Observability

`docker compose up` also starts an OpenTelemetry collector and Jaeger, so **no Honeycomb
account, key or signup is needed**. The API and worker export OTLP to the collector; the
collector forwards to Jaeger and prints spans to its own log.

The trace id is also attached to every log line as `TraceId`, and the correlation id as
`CorrelationId`. Logs are compact JSON on stdout (no file sinks), which is what a container log
collector expects. Claimant email, phone and property address are deliberately never logged;
claim id, claim number and correlation id are.

### Honeycomb

Set the key in the environment, never in a file:

```powershell
$env:HONEYCOMB_API_KEY = "hny_..."   # a real secret; not committed
docker compose up --build
```

With a key present both services export directly to `api.honeycomb.io:443` with the
`x-honeycomb-team` header. The local collector and Jaeger are then unused. In a deployed
environment the key is a container/pipeline secret (`HONEYCOMB_API_KEY`), referenced by name.

### Readiness

Both `/health/ready` endpoints open a PostgreSQL connection and, on RabbitMQ, a real AMQP
connection. Stop a dependency and the probe turns `503`:

```bash
docker compose stop postgres
curl -i http://localhost:8080/health/ready    # 503, postgres unhealthy
curl -i http://localhost:8081/health/ready    # 503, postgres unhealthy
docker compose start postgres

docker compose stop rabbitmq
curl -i http://localhost:8080/health/ready    # 503, rabbitmq unhealthy
docker compose start rabbitmq
```

The Bicep Container Apps use the same paths as liveness/readiness probes.

## Try the API directly

```bash
curl -i -X POST http://localhost:8080/api/claims \
  -H 'Content-Type: application/json' \
  -d '{
    "policyNumber": "HO-4471902",
    "firstName": "Dana",
    "lastName": "Okonjo",
    "email": "dana.okonjo@example.ca",
    "phone": "204-555-0100",
    "incidentDate": "2026-10-20",
    "lossType": "Water",
    "description": "The basement flooded after a pipe burst.",
    "estimatedLossAmount": 12500,
    "propertyAddress": {
      "addressLine1": "12 Main Street",
      "city": "Winnipeg",
      "province": "MB",
      "postalCode": "R3C 1A1"
    }
  }'
```

A valid submission returns `201 Created`, a `Location: /api/claims/{claimId}` header, and a body
shaped like:

```json
{
  "claimId": "01a16ba3-7c54-7ee9-9552-62cd08bf7ee5",
  "claimNumber": "CLM-20261024-3S2EAV",
  "submittedAt": "2026-10-24T02:00:38.7406747Z",
  "status": "Received"
}
```

Validation failures return RFC 7807 `ProblemDetails` with `errors` keyed by the camelCase
request field  nested keys use dots, e.g. `propertyAddress.postalCode` this
maps directly onto the React form fields. See [`docs/CONTRACT.md`](docs/CONTRACT.md).

## What is in the repository

| Path | Contents |
| --- | --- |
| `src/ClaimPortal.Contracts` | event contract and enum. |
| `src/ClaimPortal.Domain` | Claim aggregate, value objects, claim-number policy, repository port. |
| `src/ClaimPortal.Infrastructure` | EF Core context, mappings, migrations, transactional-outbox tables. |
| `src/ClaimPortal.Api` | Minimal API, FluentValidation rules, outbox publish path. |
| `src/ClaimPortal.Notifications` | Worker service: MassTransit consumer, Scriban templates, MailKit SMTP, idempotency. |
| `src/ClaimPortal.Observability` | Shared Serilog JSON logging, correlation-id constants, OTLP tracing, and Postgres/RabbitMQ health probes. |
| `web` | React + TypeScript + Vite + React Hook Form/Zod form. |
| `tests` | Domain (56), API (9), notification-worker (34) and Testcontainers integration (7) tests. |
| `docker` | Multi-stage Dockerfiles and the RabbitMQ delayed-message plugin image. |
| `deploy/azure` | Bicep for the optional Azure deployment, deployed and drift-checked, with the costed shape, live URLs and teardown. |
| `docs` | [`ARCHITECTURE.md`](docs/ARCHITECTURE.md), the frozen [`CONTRACT.md`](docs/CONTRACT.md), the transport notes and the worker ADR. |

## Design decisions worth knowing

**Transactional outbox.** The claim row and the outbox message are written by the same
`SaveChanges` inside one transaction. Without it, a crash between "claim saved" and "event
published" would permanently lose the confirmation email. The outbox dispatcher publishes after
commit; the persisted `MessageId` is stable across redeliveries and is the worker's
idempotency key.

**Email only in the worker.** The API publishes `ClaimSubmitted` and returns `201`. Rendering
(Scriban) and delivery (MailKit behind `IEmailSender`) live in `ClaimPortal.Notifications`.
Swapping SMTP for SendGrid or Azure Communication Services is a DI registration, not a rewrite.

**Exactly one email.** `notifications.notification_deliveries` has a unique key on the message
id and a claim/fail/mark-sent protocol. A redelivery after a crash between "SMTP accepted" and
"message acked" finds the row already `Sent` and does not send again. Transient failures retry
with exponential backoff, then delayed redelivery, then MassTransit's `_error` queue.

**Claim number.** `CLM-{yyyyMMdd}-{6 chars Crockford base32}`, date in UTC, CSPRNG-sourced
suffix excluding `I`, `L`, `O`, `U`. Uniqueness is enforced by a Postgres unique index with a
bounded retry  the generator makes collisions unlikely, the database makes them impossible.
The GUID (`claimId`, UUIDv7) is the primary key; the claim number is customer-facing, not a key.

**Two schemas, two deployables.** The worker owns `notifications`; it cannot read the claims
tables. The event carries everything the email needs (event-carried state transfer), so the
worker does not call back into the API. Loss description, estimate and address are deliberately
left out of the event.

**Transport is configuration.** `Messaging:Transport` selects `RabbitMq` (the local default) or
`AzureServiceBus` (the Azure deployment); the consumer, the `ClaimSubmitted` contract and the
outbox do not change. The switch and exactly what it does and does not abstract are documented in
[`docs/TRANSPORT.md`](docs/TRANSPORT.md). MassTransit 8.5.12 has no Azure Storage Queue transport,
so a queue-only broker would be a transport rewrite, not a configuration change; the reasoning is
in the [deployment README](deploy/azure/README.md#storage-queue-instead-of-service-bus-not-a-drop-in).

## Building and testing locally

.NET 10 SDK (10.0.402 or later), Node.js 22+ and Docker are required. The SDK version is
pinned in `global.json`; a newer feature band rolls forward automatically.

```bash
dotnet build ClaimPortal.sln
dotnet test ClaimPortal.sln          # all .NET suites; integration tests need Docker

cd web
npm ci
npm run build
npm test                             # Vitest/RTL tests
```

Tests use Shouldly (not FluentAssertions, which is commercially licensed in v8) and xUnit v3.
The notification tests run against the MassTransit test harness and SQLite; they do not need a
broker. The integration test project starts real PostgreSQL and RabbitMQ containers through
Testcontainers, so it does need Docker. End-to-end verification is the Compose stack above.

## CI/CD pipelines

Two definitions run the same eight stages, in the same order:

| Stage | What it does |
| --- | --- |
| Restore | `dotnet restore` with a NuGet cache. |
| Build | `dotnet build -c Release`, then `npm ci` and `npm run build` for the web app. |
| Unit tests | The domain, API-validation and notification-worker xUnit suites, plus the Vitest/RTL suite. |
| Integration tests | The Testcontainers suite against real PostgreSQL and RabbitMQ, then the full Compose stack: submit a claim through the web origin, assert the row is committed, and assert exactly one confirmation email reached Mailpit. |
| Playwright E2E | Headless Chromium against the full Compose stack: submit the claim form, read the claim number from the confirmation screen, then poll the Mailpit HTTP API until the confirmation email arrives and assert its body contains that number. Also covers a validation-failure submission. See [`web/e2e/README.md`](web/e2e/README.md). |
| Docker build | Build and archive the API, worker and web images. |
| Push | On non-PR builds, push `ghcr.io/amac1986/rrm-claims-portal/{api,worker,web}:sha-<commit>` (and `:latest` on `main`). |
| Deploy | Gated off by default; updates Azure Container Apps using OIDC, with no stored Azure credential. |

| Pipeline | File | What has actually been proven |
| --- | --- | --- |
| GitHub Actions | [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | **Green end to end on a real push**, including the Playwright E2E job and the GHCR push: [run 40100651398](https://github.com/Amac1986/rrm-claims-portal/actions/runs/40100651398) (commit `ef4bfdc`). |

### Credentials and the deployment gate

Neither file contains a credential.

- GitHub Actions uses its `GITHUB_TOKEN` for GHCR (job-scoped `packages: write`) no registry or cloud password is
  stored.
- Docker 29's BuildKit exports provenance and SBOM attestations by default. GHCR rejected that
  manifest list in this repository, so the pipelines build with `--provenance=false --sbom=false`
  and label images with `org.opencontainers.image.source` to associate the package with the
  repository. Removing those flags makes the push stage fail with `error from registry: denied`.

## AI use statement

This project was built with AI coding agents (Paperclip) under human direction. The work was
split into reviewed slices: contract and plan, domain/persistence, notification worker, React
form, API, and containerization. Each slice was implemented by an agent and then verified:
the code was read, projects were compiled with warnings treated as
errors, unit and component tests were run, and the full stack was exercised end to end through
Docker Compose (submit -> outbox -> RabbitMQ -> worker -> Mailpit).
