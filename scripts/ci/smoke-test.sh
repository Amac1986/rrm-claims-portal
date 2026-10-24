#!/usr/bin/env bash
#
# Integration smoke test for the full Compose stack. Used by both pipelines.
#
# It is deliberately behavioural rather than a pile of container health checks:
#   1. build and start the whole stack,
#   2. wait for the API to be ready,
#   3. POST a claim through the web origin (so nginx proxying is exercised),
#   4. assert the claim row exists in Postgres,
#   5. assert exactly one confirmation email with that claim number reached Mailpit.
#
# The last two assertions are the ones that prove the outbox -> RabbitMQ -> worker path,
# not just that the API returned 201.

set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/../.."

COMPOSE=(docker compose)
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-rrm-claims-portal-ci}"

response_file=""

cleanup() {
  status=$?
  if [ "$status" -ne 0 ]; then
    echo "Integration stack failed; dumping compose state and logs."
    "${COMPOSE[@]}" ps || true
    "${COMPOSE[@]}" logs --no-color || true
  fi
  if [ -n "$response_file" ]; then
    rm -f "$response_file"
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

echo "Starting the Compose stack..."
"${COMPOSE[@]}" up --detach --build

wait_for "API readiness" "http://localhost:8080/health/ready"
wait_for "web" "http://localhost:5173/healthz"

run_id="$(date -u +%s)-$$"
incident_date="$(date -u -d '2 days ago' +%F)"
email="ci+${run_id}@example.ca"

payload=$(cat <<JSON
{
  "policyNumber": "HO-CI-${run_id}",
  "firstName": "Ci",
  "lastName": "Smoke",
  "email": "${email}",
  "phone": "204-555-0100",
  "incidentDate": "${incident_date}",
  "lossType": "Water",
  "description": "The basement flooded after a pipe burst during the CI smoke test.",
  "estimatedLossAmount": 12500,
  "propertyAddress": {
    "addressLine1": "12 Main Street",
    "city": "Winnipeg",
    "province": "MB",
    "postalCode": "R3C 1A1"
  }
}
JSON
)

response_file=$(mktemp)

echo "Submitting a claim through the web origin..."
status=$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' \
  --request POST "http://localhost:5173/api/claims" \
  --header 'Content-Type: application/json' \
  --data "$payload")

if [ "$status" != "201" ]; then
  echo "Expected 201 Created, got ${status}." >&2
  cat "$response_file" >&2
  exit 1
fi

claim_number=$(jq --raw-output '.claimNumber // empty' "$response_file")
if [ -z "$claim_number" ]; then
  echo "The 201 response did not contain a claimNumber." >&2
  cat "$response_file" >&2
  exit 1
fi
if ! printf '%s' "$claim_number" | grep --extended-regexp --quiet '^CLM-[0-9]{8}-[0-9A-HJKMNP-TV-Z]{6}$'; then
  echo "Claim number '${claim_number}' does not match the documented format." >&2
  exit 1
fi
echo "Claim ${claim_number} was accepted."

claim_rows=$("${COMPOSE[@]}" exec --no-TTY postgres \
  psql --username claimportal --dbname claimportal --tuples-only --no-align \
  --command "select count(*) from claims where claim_number = '${claim_number}';" | tr -d '[:space:]')

if [ "$claim_rows" != "1" ]; then
  echo "Expected exactly one claims row for ${claim_number}, found '${claim_rows}'." >&2
  exit 1
fi
echo "The claim row is committed in Postgres."

echo "Waiting for the confirmation email in Mailpit..."
mail_count=0
mail_subject=""
for _ in $(seq 1 90); do
  mail_json=$(curl --fail --silent --show-error \
    "http://localhost:8025/api/v1/search?query=${claim_number}")
  mail_count=$(printf '%s' "$mail_json" | jq '.messages | length')
  if [ "$mail_count" -ge 1 ]; then
    mail_subject=$(printf '%s' "$mail_json" | jq --raw-output '.messages[0].Subject // empty')
    break
  fi
  sleep 2
done

if [ "$mail_count" -ne 1 ]; then
  echo "Expected exactly one confirmation email for ${claim_number}, found ${mail_count}." >&2
  exit 1
fi
if ! printf '%s' "$mail_subject" | grep --fixed-strings --quiet "$claim_number"; then
  echo "The email subject '${mail_subject}' does not contain the claim number." >&2
  exit 1
fi

echo "Integration smoke test passed: claim ${claim_number} was persisted and emailed exactly once."
