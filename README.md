# Red River Mutual  Home Insurance Claim Portal

A vertical slice of a home-insurance claim submission portal: a React form posts a claim to
an ASP.NET Core API, the API persists it and writes a `ClaimSubmitted` event through a
transactional outbox, a **separate** notification worker consumes that event and emails the
customer a confirmation containing the claim number.

```
Browser (React)
       POST /api/claims
     
Claims API  one transaction  PostgreSQL
                                   claims + outbox_message
      (outbox dispatcher)
     
 RabbitMQ  ClaimSubmitted  Notification worker  SMTP  Mailpit (local)
                                      
                                       notifications.notification_deliveries (idempotency)
```

The architectural rule the assignment tests is structural, not conventional: the API has no
mail library reference at all (asserted by a test), and only the worker renders and sends the
confirmation email.

## Quick start

Prerequisites: Docker Desktop (or another Docker engine) with Compose v2. Nothing else  no
cloud account, API key or signup.

```bash
docker compose up --build
```

Then:

| What | Where |
| --- | --- |
| Claim form | http://localhost:5173 |
| Mailpit (the sent email) | http://localhost:8025 |
| API health | http://localhost:8080/health/live |
| RabbitMQ management (`guest` / `guest`) | http://localhost:15672 |

Submit the form, then open Mailpit. The confirmation email contains the same claim number the
confirmation screen displayed. `docker compose down -v` followed by `docker compose up` works
again from a clean database with no manual migration step.

### Try the API directly

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
request field  nested keys use dots, e.g. `propertyAddress.postalCode`  because that is what
maps directly onto the React form fields. See [`docs/CONTRACT.md`](docs/CONTRACT.md).

## What is in the repository

| Path | Contents |
| --- | --- |
| `src/ClaimPortal.Contracts` | Frozen request/response/event contract and enum. |
| `src/ClaimPortal.Domain` | Claim aggregate, value objects, claim-number policy, repository port. |
| `src/ClaimPortal.Infrastructure` | EF Core context, mappings, migrations, transactional-outbox tables. |
| `src/ClaimPortal.Api` | Minimal API, FluentValidation rules, outbox publish path. |
| `src/ClaimPortal.Notifications` | Worker service: MassTransit consumer, Scriban templates, MailKit SMTP, idempotency. |
| `web` | React + TypeScript + Vite + React Hook Form/Zod form. |
| `tests` | Domain (56), API (9) and notification-worker (34) tests. |
| `docker` | Multi-stage Dockerfiles and the RabbitMQ delayed-message plugin image. |
| `deploy/azure` | Bicep for the optional Azure deployment, with the costed shape and teardown. Not deployed. |
| `docs` | Frozen contract and architecture decision records. |

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

**Transport is configuration.** `Messaging:Transport` selects `RabbitMq` locally; the Service
Bus path is reserved for the optional cloud deployment. The consumer does not change.

## Building and testing locally

.NET 10 SDK 10.0.402 is required. This runner starts shells with `ProgramFiles`, `APPDATA` and
`LOCALAPPDATA` unset; set them before any `dotnet` command if your environment has the same
quirk:

```powershell
$env:ProgramFiles = "C:\Program Files"
${env:ProgramFiles(x86)} = "C:\Program Files (x86)"
$env:APPDATA = "$env:USERPROFILE\AppData\Roaming"
$env:LOCALAPPDATA = "$env:USERPROFILE\AppData\Local"
```

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

Two definitions run the same seven stages, in the same order:

| Stage | What it does |
| --- | --- |
| Restore | `dotnet restore` with a NuGet cache. |
| Build | `dotnet build -c Release`, then `npm ci` and `npm run build` for the web app. |
| Unit tests | The domain, API-validation and notification-worker xUnit suites, plus the Vitest/RTL suite. |
| Integration tests | The Testcontainers suite against real PostgreSQL and RabbitMQ, then the full Compose stack: submit a claim through the web origin, assert the row is committed, and assert exactly one confirmation email reached Mailpit. |
| Docker build | Build and archive the API, worker and web images. |
| Push | On non-PR builds, push `ghcr.io/amac1986/rrm-claims-portal/{api,worker,web}:sha-<commit>` (and `:latest` on `main`). |
| Deploy | Gated off by default; updates Azure Container Apps using OIDC, with no stored Azure credential. |

| Pipeline | File | What has actually been proven |
| --- | --- | --- |
| Azure DevOps (**primary**) | [`azure-pipelines.yml`](azure-pipelines.yml) | **Not run.** The definition is complete and was validated against Microsoft's published pipeline schema, but no Azure DevOps organization or billing link exists, so Microsoft-hosted parallelism is not granted. |
| GitHub Actions (no-setup fallback) | [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | **Green end to end on a real push**, including the GHCR push: [run 40055091388](https://github.com/Amac1986/rrm-claims-portal/actions/runs/40055091388) (commit `3a2f81f`). |

The Azure DevOps gate is not a code defect. Microsoft does not grant the free Microsoft-hosted
parallelism tier automatically: the organization must be linked to an Azure subscription with
billing configured. Until that is done, the first ADO run fails with
`No hosted parallelism has been purchased or granted`; the documented remedy is a request form
that community reports put at 2-3 business days. Do not read `azure-pipelines.yml` as a pipeline
that has run.

### Credentials and the deployment gate

Neither file contains a credential.

- GitHub Actions uses its `GITHUB_TOKEN` for GHCR (job-scoped `packages: write`) and GitHub OIDC
  workload-identity federation for Azure (`id-token: write`). No registry or cloud password is
  stored.
- Azure DevOps expects pipeline secret variables `GHCR_USER` and `GHCR_TOKEN` for the GHCR push,
  and a workload-identity-federated service connection named `rrm-claims-portal-azure` for deploy.
  The deploy target is selected by the `AZURE_RESOURCE_GROUP` and
  `AZURE_CONTAINER_APP_{API,WORKER,WEB}` variables, and the stage only runs when the
  `deployEnabled` queue-time parameter is set.
- Deployment is cost-gated. The infrastructure-as-code lives in [`deploy/azure/`](deploy/azure/README.md);
  it has been compiled and linted but not deployed, and no Azure resource has been created for
  this project. The deploy stage is provided for the owner to arm after the cost decision, not
  evidence that a cloud deployment happened.
- Docker 29's BuildKit exports provenance and SBOM attestations by default. GHCR rejected that
  manifest list in this repository, so the pipelines build with `--provenance=false --sbom=false`
  and label images with `org.opencontainers.image.source` to associate the package with the
  repository. Removing those flags makes the push stage fail with `error from registry: denied`.

## Azure deployment (Bicep, not deployed)

[`deploy/azure/`](deploy/azure/README.md) contains the Bicep for the optional Azure shape:
Container Apps for the API, worker, web and Mailpit, PostgreSQL Flexible Server B1ms, Service
Bus Standard, a Free-tier Static Web App and a capped Log Analytics workspace. Every app has
`minReplicas: 0`; the worker scales on Service Bus queue depth through KEDA, so the stack costs
nothing at rest and stays inside the Container Apps Consumption free grant at demo volume.

The template is **authored and offline-validated, not deployed**. `bicep build` and `bicep lint`
both pass with no diagnostics. `az deployment group what-if` has not been run because this build
host has no Azure CLI or authenticated subscription. The priced shape, the cost gate, the exact
commands and the teardown are in the [deployment README](deploy/azure/README.md#cost). In short:
about $10/month with the free-account PostgreSQL offer applying, about $31/month otherwise, with
Service Bus Standard as the recurring line item. Nothing billable has been created.

A live deployment is blocked on the FIR-9 Service Bus transport swap: both the API and the worker
still throw `NotSupportedException` when `Messaging:Transport` is `AzureServiceBus`. The Bicep
sets the target configuration honestly rather than pretending the apps work today. The Bicep
defines a free-tier Static Web App but does not wire it; the Container Apps `web` app is the
working front door once the Service Bus transport lands.

Teardown, once the owner has deployed and finished with it:

```bash
az group delete --name rrm-claims-portal --yes --no-wait
```

## Verified status (honest notes)

- `docker compose up --build` brings up Postgres, RabbitMQ (with the delayed-message plugin),
  Mailpit, the API, the worker and the web app.
- A claim submitted through the React/web origin returns `201`, appears in Postgres with an
  outbox row, is consumed by the worker, and arrives in Mailpit with the same claim number. A
  second claim was verified the same way after the JSON timestamp/enum fixes.
- Invalid submissions return the expected camelCase/dotted error keys.
- The API, domain and worker build with zero warnings; the .NET and web suites pass.
- **Not yet verified:** Playwright browser tests (the optional E2E task) and a live Azure
  deployment. The Bicep is committed and offline-validated, but it is cost-gated and was not
  deployed; `what-if` was not run because the build host has no authenticated subscription.
  Azure DevOps is provided but has not run (see
  [CI/CD pipelines](#cicd-pipelines)); GitHub Actions is proven green. No claim is made that
  unrun pipelines or infrastructure work.

## AI use statement

This project was built with AI coding agents (Paperclip) under human direction. The work was
split into reviewed slices: contract and plan, domain/persistence, notification worker, React
form, API, and containerization. Each slice was implemented by an agent and then verified by the
substitute development lead: the code was read, projects were compiled with warnings treated as
errors, unit and component tests were run, and the full stack was exercised end to end through
Docker Compose (submit -> outbox -> RabbitMQ -> worker -> Mailpit).

Validation was behavioural rather than a claim of correctness: contract rules have unit tests,
the API's "no email dependency" rule is an architecture test, the consumer's
consume/fault/retry/deduplicate paths are test-harness tests, and the real stack was run to
confirm the asynchronous path. Security choices were applied deliberately: email is generated
and sent only in the worker; no SMTP or broker credentials are committed (local defaults are
throwaway, deployed values are referenced by name); the API returns ProblemDetails without
exception detail; claimant email, phone and address are not logged; the event omits loss
details and property address. No AI output was treated as verified until it compiled and the
tests or the running stack showed it working.
