#!/usr/bin/env bash
# Runs the init function (database roles and schema) inside the private network and fails when it fails
# (spec 001 RF-010, plan §4.3 step 5). Run after the stack servicio and before the stacks eventos and rutas.
# The response is checked but not printed; the function only logs the SQLSTATE or exception type, never connection data.
# Usage: scripts/run-init.sh <function-name>      (needs AWS credentials and jq; the AWS CLI reads AWS_REGION)
set -euo pipefail

FUNCTION=${1:?usage: run-init.sh <function-name>}
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# Terraform already waits for both states; this keeps the script safe to run on its own.
aws lambda wait function-active-v2 --function-name "$FUNCTION"
aws lambda wait function-updated-v2 --function-name "$FUNCTION"

aws lambda invoke --function-name "$FUNCTION" --log-type Tail \
  --cli-binary-format raw-in-base64-out --payload '{}' "$TMP/response.json" > "$TMP/meta.json"

echo "--- Last log lines of $FUNCTION ---"
jq -r '.LogResult // empty' "$TMP/meta.json" | base64 -d || true

if [ -n "$(jq -r '.FunctionError // empty' "$TMP/meta.json")" ]; then
  echo "::error::The init function $FUNCTION failed (network, secrets, TLS or SQL): see the log above."
  exit 1
fi
if [ "$(jq -c '.' "$TMP/response.json" 2>/dev/null)" != '{"ok":true}' ]; then
  echo "::error::The init function $FUNCTION did not answer {\"ok\":true}."
  exit 1
fi
echo "Init done: roles audit_writer and audit_reader and schema 001_audit_store ready."
