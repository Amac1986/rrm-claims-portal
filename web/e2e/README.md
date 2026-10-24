# Playwright end-to-end tests

These tests drive a real Chromium browser against the **whole `docker compose` stack**:
React form -> API -> Postgres/outbox -> RabbitMQ -> notification worker -> SMTP -> Mailpit.
They do not start or stop the stack themselves.

## Run

```bash
# from the repository root
docker compose up --build -d

# wait until the API and web are ready
curl -f http://localhost:8080/health/ready
curl -f http://localhost:5173/healthz

cd web
npx playwright install chromium   # once per machine
npm run test:e2e
```

Defaults: `E2E_BASE_URL=http://localhost:5173` and `MAILPIT_URL=http://localhost:8025`.
Override either for a different host/port. The suite runs headless, with one worker.

To prove a clean stack, `docker compose down -v` before `up`, and run the suite twice.

## What is covered

- **Primary journey** (`claim-submission.spec.ts`): fill the form, submit, read the claim
  number from the confirmation screen, then poll Mailpit's HTTP API until the confirmation
  email arrives and assert its subject and **body** contain that same claim number. This is
  the assertion that proves the asynchronous path across processes and the broker.
- **Validation failure** (`validation.spec.ts`): submit an invalid form, assert the errors
  appear on the right fields (via `aria-invalid` and the alert's `aria-describedby`), and
  assert no `POST /api/claims` request was made, so nothing was persisted or emailed.

Email polling uses a bounded timeout (45s) and a short interval, never a fixed sleep.

## CI wiring

This task owns `web/` only; the pipeline files are owned separately. The integration job
needs, after the stack is healthy and before `docker compose down`:

```bash
cd web
npm ci
npx playwright install --with-deps chromium
npm run test:e2e
```

Upload `web/test-results/` and `web/playwright-report/` as artifacts on failure.
