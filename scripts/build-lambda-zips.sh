#!/usr/bin/env bash
# Builds the two Lambda packages (runtime dotnet10, x86_64, framework-dependent) as deterministic .zip files: the same
# sources give the same bytes, so Terraform's source_code_hash only changes when the code or a dependency changes
# (spec 001 RNF-005; plan D-001, D-002, D-012).
#   build/api.zip        src/API     (ASP.NET Core over the Lambda hosting; function urban-alert-auditoria-api)
#   build/funciones.zip  src/Lambda  (handlers of urban-alert-auditoria-registro and -init)
# Usage: scripts/build-lambda-zips.sh [output-dir]      (default: build; needs dotnet, zip)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="${1:-$ROOT/build}"
mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
# Lambda limit for a direct upload (Terraform `filename`); above it the package would have to go through S3.
MAX_BYTES=52428800

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

package() {
  local project="$1" name="$2"
  local pkg="$OUT_DIR/pkg-$name" out="$OUT_DIR/$name.zip"
  rm -rf "$pkg" "$out"

  # ContinuousIntegrationBuild: deterministic paths in the assemblies and PDBs. No apphost: Lambda runs `dotnet <dll>`.
  dotnet publish "$ROOT/$project" --configuration Release --runtime linux-x64 --self-contained false \
    --output "$pkg" --nologo --verbosity quiet \
    -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -p:GenerateDocumentationFile=false

  # Fixed permissions and dates, sorted entries, no extra attributes (-X) or directory entries (-D).
  chmod -R u=rwX,go=rX "$pkg"
  find "$pkg" -exec touch -h -t 198001010000 {} +
  (cd "$pkg" && find . -type f | LC_ALL=C sort | sed 's|^\./||' | zip -q -X -D -@ "$out")
  rm -rf "$pkg"

  local size
  size=$(wc -c < "$out" | tr -d ' ')
  echo "Package: $out ($size bytes, sha256 $(sha256 "$out"))"
  if [ "$size" -gt "$MAX_BYTES" ]; then
    echo "::error::The Lambda package $name.zip is $size bytes, above the $MAX_BYTES bytes of a direct upload."
    exit 1
  fi
}

package src/API/Auditoria.API.csproj api
package src/Lambda/Auditoria.Lambda.csproj funciones
