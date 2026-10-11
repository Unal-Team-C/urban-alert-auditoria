#!/usr/bin/env bash
# Manual end-to-end check in AWS (spec 001 MS-001, MS-002): creates N reports in Reportes through the shared gateway,
# waits up to 60 s for each ReporteCreado event to appear in the Auditoria history (Reportes → EventBridge → rule →
# SQS → registro → RDS), checks the integrity of each report, and deletes the reports. The audit records stay: the
# chain is append-only. Not run by Provision: it writes data and emits events.
# Usage: scripts/crear-reporte-y-auditar.sh https://<api-id>.execute-api.<region>.amazonaws.com [-n N]   (needs curl, jq)
set -euo pipefail

if [[ $# -lt 1 || "$1" != https://* ]]; then
  echo "usage: $0 https://<api-id>.execute-api.<region>.amazonaws.com [-n N]" >&2
  exit 2
fi
gateway="${1%/}"
count=1
if [[ "${2:-}" == "-n" ]]; then
  count="${3:?missing N}"
fi
reportes="$gateway/api/v1/reportes"
auditoria="$gateway/api/v1/auditoria/reportes"
wait_s="${AUDIT_WAIT_S:-60}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0
created=()

pass() { echo "ok   $1"; }
fail() {
  echo "FAIL $1: $(head -c 300 "$work/body" 2>/dev/null)"
  failures=$((failures + 1))
}

# Inside the Bogotá range that Geoespacial accepts.
json='{"categoria":"VIAS_Y_ANDENES","tipo":"HUECOS_EN_LA_VIA","descripcion":"Prueba de auditoría en AWS (se borra al terminar)","latitud":4.6512,"longitud":-74.0561,"urlImagen":"https://example.com/prueba.jpg"}'
for ((i = 1; i <= count; i++)); do
  code="$(curl -s -o "$work/body" -w '%{http_code}' --max-time 30 -X POST -H 'Content-Type: application/json' -d "$json" "$reportes" || true)"
  if [[ "$code" == "201" ]] && id="$(jq -er '.idReporte' "$work/body")"; then
    created+=("$id")
    pass "POST /api/v1/reportes -> 201 ($id)"
  else
    fail "POST /api/v1/reportes -> ${code:-no answer}, expected 201"
  fi
done

for id in "${created[@]}"; do
  start=$SECONDS
  found=0
  while ((SECONDS - start < wait_s)); do
    code="$(curl -s -o "$work/body" -w '%{http_code}' --max-time 30 "$auditoria/$id" || true)"
    if [[ "$code" == "200" ]] && [[ "$(jq -r '[.items[] | select(.eventType == "reporte.creado")] | length' "$work/body")" == 1 ]]; then
      found=1
      break
    fi
    sleep 3
  done
  if ((found)); then
    pass "audit event of $id after $((SECONDS - start)) s"
  else
    fail "no reporte.creado event for $id after $wait_s s"
    continue
  fi

  code="$(curl -s -o "$work/body" -w '%{http_code}' --max-time 30 "$auditoria/$id/integridad" || true)"
  if [[ "$code" == "200" ]] && jq -e '.verified == true' "$work/body" >/dev/null; then
    pass "integrity of $id verified"
  else
    fail "GET /api/v1/auditoria/reportes/$id/integridad -> ${code:-no answer}, expected verified"
  fi
done

for id in "${created[@]}"; do
  code="$(curl -s -o "$work/body" -w '%{http_code}' --max-time 15 -X DELETE "$reportes/$id" || true)"
  if [[ "$code" == "204" ]]; then pass "DELETE /api/v1/reportes/$id -> 204"; else fail "DELETE /api/v1/reportes/$id -> ${code:-no answer}, expected 204"; fi
done

if ((failures > 0)); then
  echo "$failures check(s) failed"
  exit 1
fi
echo "Every created report was audited and its chain is intact."
