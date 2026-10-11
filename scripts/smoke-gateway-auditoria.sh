#!/usr/bin/env bash
# Smoke test after Provision (plan §4.3 step 8), without writing data:
#  - the two public routes answer through the gateway (gateway → Lambda → Secrets Manager → RDS);
#  - nothing else of the service is published (health, invalid ids);
#  - the queue feeds the registro function and the Auditoria rule is enabled; the DLQ is reported (warning only).
# Usage: scripts/smoke-gateway-auditoria.sh <gateway-url> <event-source-mapping-uuid> <rule-name> <event-bus-arn> <dlq-url>
# Needs curl, jq and AWS credentials (the AWS CLI reads AWS_REGION). SMOKE_ATTEMPTS / SMOKE_SLEEP_S tune the retries.
set -euo pipefail

GATEWAY=${1:?usage: smoke-gateway-auditoria.sh <gateway-url> <esm-uuid> <rule-name> <event-bus-arn> <dlq-url>}
ESM_UUID=${2:?missing event source mapping uuid}
RULE=${3:?missing rule name}
BUS=${4:?missing event bus arn}
DLQ_URL=${5:?missing dlq url}
ATTEMPTS=${SMOKE_ATTEMPTS:-12}
SLEEP_S=${SMOKE_SLEEP_S:-10}
GATEWAY=${GATEWAY%/}
BASE="$GATEWAY/api/v1/auditoria/reportes"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
REPORT_ID=$(cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen | tr '[:upper:]' '[:lower:]')

status() { curl --silent --output "$TMP/body" --write-out '%{http_code}' --max-time 35 "$1" || echo 000; }

# New routes and cold starts (VPC + .NET) can take a while: retry the first call.
code=000
for ((i = 1; i <= ATTEMPTS; i++)); do
  code=$(status "$BASE/$REPORT_ID")
  [[ "$code" == 200 ]] && break
  echo "Attempt $i/$ATTEMPTS: GET /api/v1/auditoria/reportes/{id} -> $code"
  sleep "$SLEEP_S"
done
if [[ "$code" != 200 ]] || [[ "$(jq -r '.items | length' "$TMP/body" 2>/dev/null)" != 0 ]]; then
  echo "::error::GET /api/v1/auditoria/reportes/{id} answered $code (expected 200 with an empty history)."
  exit 1
fi
echo "ok   history of an unknown report: 200, empty"

code=$(status "$BASE/$REPORT_ID/integridad")
if [[ "$code" != 200 ]]; then
  echo "::error::GET /api/v1/auditoria/reportes/{id}/integridad answered $code (expected 200)."
  exit 1
fi
echo "ok   integrity: 200"

for path in /api/v1/auditoria/health /api/v1/auditoria/reportes/no-es-un-guid; do
  code=$(status "$GATEWAY$path")
  if [[ "$code" != 404 ]]; then
    echo "::error::GET $path answered $code (expected 404: only the two routes are published)."
    exit 1
  fi
  echo "ok   $path: 404"
done

state=$(aws lambda get-event-source-mapping --uuid "$ESM_UUID" --query State --output text)
if [[ "$state" != Enabled ]]; then
  echo "::error::The queue → registro mapping is $state (expected Enabled)."
  exit 1
fi
echo "ok   queue → registro mapping: Enabled"

state=$(aws events describe-rule --name "$RULE" --event-bus-name "$BUS" --query State --output text)
if [[ "$state" != ENABLED ]]; then
  echo "::error::The rule $RULE is $state (expected ENABLED)."
  exit 1
fi
echo "ok   rule $RULE: ENABLED"

pending=$(aws sqs get-queue-attributes --queue-url "$DLQ_URL" --attribute-names ApproximateNumberOfMessages \
  --query 'Attributes.ApproximateNumberOfMessages' --output text)
if [[ "$pending" != 0 ]]; then
  echo "::warning::The dead-letter queue has $pending message(s): events that could not be audited (README, «Cola de fallidos»)."
else
  echo "ok   dead-letter queue: empty"
fi
echo "Smoke test passed."
