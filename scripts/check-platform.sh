#!/usr/bin/env bash
# Fails before anything is changed when a shared dependency is missing (spec 001-migracion-aws RF-016):
#  - the shared platform (Unal-Team-C/urban-alert-plataforma): every parameter of its SSM contract must exist;
#  - the EventBridge bus that replaces RabbitMQ (created by hand, not managed by any repo).
# Usage: scripts/check-platform.sh [event-bus-arn]   (default: event_bus_arn of infra/eventos/terraform.tfvars)
# Needs AWS credentials; the AWS CLI reads AWS_REGION. describe-event-bus needs the bootstrap policy
# github-actions-urban-alert-auditoria (infra/bootstrap/README.md).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
PREFIX="${PLATFORM_SSM_PREFIX:-/urban-alert/plataforma/}"
REQUIRED=(region vpc-id vpc-cidr subredes-privadas api-id api-endpoint api-execution-arn vpc-link-id vpc-link-sg-id)
EVENT_BUS_ARN="${1:-$(sed -n 's/^event_bus_arn *= *"\(.*\)"$/\1/p' "$here/../infra/eventos/terraform.tfvars")}"

if [[ -z "$EVENT_BUS_ARN" ]]; then
  echo "::error::No se encontró event_bus_arn en infra/eventos/terraform.tfvars."
  exit 1
fi

names=()
for parameter in "${REQUIRED[@]}"; do
  names+=("${PREFIX}${parameter}")
done

# GetParameters returns the missing names instead of failing, so one call tells exactly what is absent.
missing=$(aws ssm get-parameters --names "${names[@]}" --query 'InvalidParameters' --output text)

if [[ -n "$missing" && "$missing" != "None" ]]; then
  echo "::error::La plataforma compartida no está aprovisionada (urban-alert-plataforma): faltan ${missing//$'\t'/ }"
  echo "Aprovisiona primero la plataforma (workflow Provision de Unal-Team-C/urban-alert-plataforma)."
  exit 1
fi
echo "Plataforma compartida disponible (${#REQUIRED[@]} parámetros en ${PREFIX})."

set +e
bus_output="$(aws events describe-event-bus --name "$EVENT_BUS_ARN" --query Arn --output text 2>&1)"
bus_code=$?
set -e

if [[ $bus_code -ne 0 ]]; then
  case "$bus_output" in
    *ResourceNotFoundException*)
      echo "::error::No existe el bus de EventBridge $EVENT_BUS_ARN (se crea a mano, fuera de todo repo)."
      ;;
    *AccessDenied*)
      echo "::error::El rol de los workflows no puede describir el bus $EVENT_BUS_ARN: falta la política github-actions-urban-alert-auditoria (infra/bootstrap/README.md)."
      ;;
    *)
      echo "::error::No se pudo comprobar el bus de EventBridge $EVENT_BUS_ARN: $bus_output"
      ;;
  esac
  exit 1
fi
echo "Bus de EventBridge disponible ($bus_output)."
