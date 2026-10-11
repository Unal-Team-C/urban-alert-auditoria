#!/usr/bin/env bash
# Tests check-platform.sh with a fake `aws` on the PATH (no AWS credentials needed).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
script="$here/../check-platform.sh"
fake_bin="$(mktemp -d)"
trap 'rm -rf "$fake_bin"' EXIT
bus="arn:aws:events:us-east-2:348364742283:event-bus/urban-alert-bus-eventos"

# The fake CLI prints $FAKE_MISSING as the InvalidParameters of `aws ssm get-parameters`, and answers
# `aws events describe-event-bus` according to $FAKE_BUS (ok | missing | denied).
cat >"$fake_bin/aws" <<'FAKE'
#!/usr/bin/env bash
case "$1 $2" in
  "ssm get-parameters") echo "${FAKE_MISSING:-}" ;;
  "events describe-event-bus")
    [[ "$4" == "${FAKE_EXPECTED_BUS:-}" ]] || { echo "unexpected bus: $4" >&2; exit 98; }
    case "${FAKE_BUS:-ok}" in
      ok) echo "$4" ;;
      missing) echo "An error occurred (ResourceNotFoundException) when calling the DescribeEventBus operation: Event bus x does not exist." >&2; exit 254 ;;
      denied) echo "An error occurred (AccessDeniedException) when calling the DescribeEventBus operation: not authorized" >&2; exit 254 ;;
    esac
    ;;
  *) echo "unexpected call: $*" >&2; exit 99 ;;
esac
FAKE
chmod +x "$fake_bin/aws"

failures=0
check() {
  local name="$1" expected_code="$2" expected_text="$3" output code
  set +e
  output="$(PATH="$fake_bin:$PATH" FAKE_EXPECTED_BUS="$bus" bash "$script" 2>&1)"
  code=$?
  set -e
  if [[ "$code" == "$expected_code" && "$output" == *"$expected_text"* ]]; then
    echo "ok   $name"
  else
    echo "FAIL $name (exit $code): $output"
    failures=$((failures + 1))
  fi
}

FAKE_MISSING="" check "platform and bus present (bus from infra/eventos/terraform.tfvars)" 0 "Bus de EventBridge disponible ($bus)"
FAKE_MISSING="None" check "CLI prints None when nothing is missing" 0 "Plataforma compartida disponible"
FAKE_MISSING="/urban-alert/plataforma/api-id" check "one parameter missing" 1 "plataforma compartida no está aprovisionada"
FAKE_MISSING=$'/urban-alert/plataforma/vpc-id\t/urban-alert/plataforma/api-id' check "several missing are listed" 1 "/urban-alert/plataforma/vpc-id /urban-alert/plataforma/api-id"
FAKE_MISSING="" FAKE_BUS=missing check "bus does not exist" 1 "No existe el bus de EventBridge"
FAKE_MISSING="" FAKE_BUS=denied check "bootstrap policy missing" 1 "falta la política github-actions-urban-alert-auditoria"

exit "$failures"
