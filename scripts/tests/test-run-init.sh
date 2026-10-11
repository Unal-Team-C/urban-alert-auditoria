#!/usr/bin/env bash
# Tests run-init.sh with a fake `aws` on the PATH (no AWS credentials needed).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../run-init.sh"
fake_bin="$(mktemp -d)"
trap 'rm -rf "$fake_bin"' EXIT

# `aws lambda invoke` writes $FAKE_RESPONSE to the outfile (last argument) and prints the metadata; with
# FAKE_ERROR=1 the metadata carries FunctionError, as when the function throws.
cat >"$fake_bin/aws" <<'FAKE'
#!/usr/bin/env bash
case "$1 $2" in
  "lambda wait") exit 0 ;;
  "lambda invoke")
    outfile="${*: -1}"
    printf '%s' "${FAKE_RESPONSE:-}" > "$outfile"
    log=$(printf 'Base de auditoría inicializada\n' | base64 | tr -d '\n')
    if [[ "${FAKE_ERROR:-0}" == 1 ]]; then
      printf '{"StatusCode":200,"FunctionError":"Unhandled","LogResult":"%s"}' "$log"
    else
      printf '{"StatusCode":200,"LogResult":"%s"}' "$log"
    fi
    ;;
  *) echo "unexpected call: $*" >&2; exit 99 ;;
esac
FAKE
chmod +x "$fake_bin/aws"

failures=0
check() {
  local name="$1" expected_code="$2" expected_text="$3" output code
  set +e
  output="$(PATH="$fake_bin:$PATH" bash "$script" urban-alert-auditoria-init 2>&1)"
  code=$?
  set -e
  if [[ "$code" == "$expected_code" && "$output" == *"$expected_text"* && "$output" != *"secreto-que-no-debe-salir"* ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name (exit $code): $output"
    failures=$((failures + 1))
  fi
}

FAKE_RESPONSE='{"ok":true}' check "init succeeds" 0 "Init done"
FAKE_RESPONSE='{"errorMessage":"Host=x;Password=secreto-que-no-debe-salir"}' FAKE_ERROR=1 check "function error fails without printing the response" 1 "failed (network, secrets, TLS or SQL)"
FAKE_RESPONSE='{"ok":false,"detalle":"secreto-que-no-debe-salir"}' check "unexpected response fails without printing it" 1 'did not answer {"ok":true}'

set +e
output="$(PATH="$fake_bin:$PATH" bash "$script" 2>&1)"
code=$?
set -e
if [[ $code -ne 0 && "$output" == *"usage: run-init.sh"* ]]; then echo "ok   usage without arguments"; else echo "FAIL usage: $output"; failures=$((failures + 1)); fi

exit "$failures"
