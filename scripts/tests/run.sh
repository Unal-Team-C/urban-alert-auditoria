#!/usr/bin/env bash
# Runs every scripts/tests/test-*.sh (fake `aws`/`curl` on the PATH: no AWS credentials needed). Exit code = failures.
set -uo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
failures=0
for test in "$here"/test-*.sh; do
  echo "== $(basename "$test")"
  bash "$test" || failures=$((failures + 1))
done
[[ $failures -eq 0 ]] && echo "All script tests passed." || echo "$failures script test file(s) failed."
exit "$failures"
