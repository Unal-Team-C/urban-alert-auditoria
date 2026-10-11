#!/usr/bin/env bash
# Tests smoke-gateway-auditoria.sh with fake `curl` and `aws` on the PATH (no network, no AWS credentials).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../smoke-gateway-auditoria.sh"
fake_bin="$(mktemp -d)"
trap 'rm -rf "$fake_bin"' EXIT

# Fake curl: answers by path. FAKE_HISTORY / FAKE_HEALTH / FAKE_BAD_ID are the codes; the body goes to --output.
cat >"$fake_bin/curl" <<'FAKE'
#!/usr/bin/env bash
out=""; url="${*: -1}"
while [[ $# -gt 0 ]]; do [[ "$1" == "--output" ]] && out="$2"; shift; done
case "$url" in
  */health) code=${FAKE_HEALTH:-404}; body='' ;;
  */no-es-un-guid) code=${FAKE_BAD_ID:-404}; body='' ;;
  */integridad) code=200; body='{"verified":true}' ;;
  */api/v1/auditoria/reportes/*) code=${FAKE_HISTORY:-200}; body=${FAKE_BODY:-'{"reportId":"x","items":[],"nextCursor":null}'} ;;
  *) code=500; body='' ;;
esac
printf '%s' "$body" > "$out"
printf '%s' "$code"
FAKE
cat >"$fake_bin/aws" <<'FAKE'
#!/usr/bin/env bash
case "$1 $2" in
  "lambda get-event-source-mapping") echo "${FAKE_ESM:-Enabled}" ;;
  "events describe-rule") echo "${FAKE_RULE:-ENABLED}" ;;
  "sqs get-queue-attributes") echo "${FAKE_DLQ:-0}" ;;
  *) echo "unexpected call: $*" >&2; exit 99 ;;
esac
FAKE
chmod +x "$fake_bin/curl" "$fake_bin/aws"

failures=0
check() {
  local name="$1" expected_code="$2" expected_text="$3" output code
  set +e
  output="$(PATH="$fake_bin:$PATH" SMOKE_ATTEMPTS=2 SMOKE_SLEEP_S=0 bash "$script" https://api123.execute-api.us-east-2.amazonaws.com/ \
    esm-uuid urban-alert-auditoria-reporte-creado arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos \
    https://sqs.us-east-2.amazonaws.com/348364742283/urban-alert-auditoria-eventos-dlq 2>&1)"
  code=$?
  set -e
  if [[ "$code" == "$expected_code" && "$output" == *"$expected_text"* ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name (exit $code): $output"
    failures=$((failures + 1))
  fi
}

check "everything in place" 0 "Smoke test passed."
FAKE_HISTORY=503 check "API not reachable" 1 "answered 503"
FAKE_BODY='{"items":[{"sequence":1}]}' check "history of a random id is not empty" 1 "expected 200 with an empty history"
FAKE_HEALTH=200 check "health must not be published" 1 "/api/v1/auditoria/health answered 200"
FAKE_BAD_ID=500 check "invalid id must be 404" 1 "no-es-un-guid answered 500"
FAKE_ESM=Disabled check "mapping disabled" 1 "mapping is Disabled"
FAKE_RULE=DISABLED check "rule disabled" 1 "is DISABLED"
FAKE_DLQ=3 check "messages in the DLQ only warn" 0 "::warning::The dead-letter queue has 3"

exit "$failures"
