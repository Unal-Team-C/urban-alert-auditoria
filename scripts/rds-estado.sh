#!/usr/bin/env bash
# Stops or starts the RDS instance and waits for the end state (Pausar / Provision after a pause).
# Why not Terraform: aws_rds_instance_state (provider 6.68) waits for "available" before starting, and compares
# with the state it saved, not the live one, so it fails in both directions of a pause/resume cycle.
# Usage: scripts/rds-estado.sh detener|arrancar <instance id>
#   detener:  available → stopped (Pausar). Already stopped: nothing to do.
#   arrancar: stopped → available (Provision, before the base stack). Missing instance (first deploy), already
#             available or any other state: nothing to start (it only waits while it is starting).
# Needs AWS credentials. ESPERA_S / INTENTOS tune the polling (default 20 s × 90 = 30 min).
set -euo pipefail

if [[ $# -ne 2 || ( "$1" != detener && "$1" != arrancar ) ]]; then
  echo "usage: $0 detener|arrancar <instance id>" >&2
  exit 2
fi
accion="$1"
instancia="$2"

estado() {
  aws rds describe-db-instances --db-instance-identifier "$instancia" --query 'DBInstances[0].DBInstanceStatus' --output text
}

err="$(mktemp)"
trap 'rm -f "$err"' EXIT
if ! actual="$(estado 2>"$err")"; then
  if grep -q DBInstanceNotFound "$err"; then
    if [[ "$accion" == arrancar ]]; then
      echo "Instance $instancia does not exist yet: nothing to start."
      exit 0
    fi
    echo "::error::Instance $instancia does not exist: nothing to stop." >&2
    exit 1
  fi
  cat "$err" >&2
  exit 1
fi

if [[ "$accion" == detener ]]; then
  objetivo=stopped
  case "$actual" in
    available)
      aws rds stop-db-instance --db-instance-identifier "$instancia" >/dev/null
      echo "Stopping $instancia…"
      ;;
    stopped | stopping) ;;
    *)
      echo "::error::Instance $instancia is $actual: it can only be stopped when available. Try again later." >&2
      exit 1
      ;;
  esac
else
  objetivo=available
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
fi

for _ in $(seq 1 "${INTENTOS:-90}"); do
  actual="$(estado)"
  if [[ "$actual" == "$objetivo" ]]; then
    echo "Instance $instancia is $objetivo."
    exit 0
  fi
  sleep "${ESPERA_S:-20}"
done
echo "::error::Instance $instancia is still $actual, expected $objetivo." >&2
exit 1
