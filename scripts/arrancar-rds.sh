#!/usr/bin/env bash
# Starts the RDS instance if it is stopped (after a pause) and waits until it is available.
# Why not Terraform: aws_rds_instance_state waits for "available" before starting, so it fails on a stopped
# instance (provider 6.68: "unexpected state 'stopped'"). Once the instance runs, its state matches the
# configuration and Terraform changes nothing. Stopping still goes through Terraform (pausada = true).
# Usage: scripts/arrancar-rds.sh <instance id>
#   Missing instance (first deploy) or any state other than stopped: nothing to start, only waits when it is
#   starting. Needs AWS credentials. ESPERA_S / INTENTOS tune the polling (default 20 s × 90 = 30 min).
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <instance id>" >&2
  exit 2
fi
instancia="$1"

estado() {
  aws rds describe-db-instances --db-instance-identifier "$instancia" --query 'DBInstances[0].DBInstanceStatus' --output text
}

err="$(mktemp)"
trap 'rm -f "$err"' EXIT
if ! actual="$(estado 2>"$err")"; then
  if grep -q DBInstanceNotFound "$err"; then
    echo "Instance $instancia does not exist yet: nothing to start."
    exit 0
  fi
  cat "$err" >&2
  exit 1
fi

case "$actual" in
  stopped)
    aws rds start-db-instance --db-instance-identifier "$instancia" >/dev/null
    echo "Starting $instancia…"
    ;;
  starting) echo "Instance $instancia is already starting." ;;
  *)
    echo "Instance $instancia is $actual: nothing to start."
    exit 0
    ;;
esac

for _ in $(seq 1 "${INTENTOS:-90}"); do
  actual="$(estado)"
  if [[ "$actual" == available ]]; then
    echo "Instance $instancia is available."
    exit 0
  fi
  sleep "${ESPERA_S:-20}"
done
echo "::error::Instance $instancia is still $actual, expected available." >&2
exit 1
