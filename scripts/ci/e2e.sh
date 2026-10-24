#!/usr/bin/env bash
#
# Playwright end-to-end job for the full Compose stack. Used by both pipelines.
#
# The suite itself is documented in web/e2e/README.md. This wrapper owns the stack
# lifecycle the tests deliberately do not: install the browser, build and start the stack,
# wait for every entry point the browser and the Mailpit assertions need, run the suite,
# and always tear the stack down. It reuses the same readiness-by-polling approach as
# scripts/ci/smoke-test.sh instead of fixed sleeps.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

COMPOSE=(docker compose)
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-rrm-claims-portal-e2e-ci}"

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "Playwright E2E failed; dumping compose state and logs."
    "${COMPOSE[@]}" ps || true
    "${COMPOSE[@]}" logs --no-color || true
  fi
  "${COMPOSE[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  exit "$status"
}
trap cleanup EXIT

wait_for() {
  local name=$1
  local url=$2
  local attempts=${3:-120}

  for _ in $(seq 1 "$attempts"); do
    if curl --fail --silent --show-error --max-time 5 "$url" >/dev/null 2>&1; then
      echo "$name is up at $url"
      return 0
    fi
    sleep 2
  done

  echo "$name did not become ready at $url" >&2
  return 1
}

# Install the suite's dependencies before starting containers so a broken lockfile fails fast.
echo "Installing web dependencies and Playwright Chromium..."
(
  cd web
  npm ci
  npx playwright install --with-deps chromium
)

echo "Starting the Compose stack..."
"${COMPOSE[@]}" up --detach --build

# The browser needs the web origin and the API behind it; the email assertion needs Mailpit.
wait_for "API readiness" "http://localhost:8080/health/ready"
wait_for "web" "http://localhost:5173/healthz"
wait_for "Mailpit" "http://localhost:8025/api/v1/messages"

echo "Running the Playwright end-to-end suite..."
(
  cd web
  npm run test:e2e
)
