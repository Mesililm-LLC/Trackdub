#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
mkdir -p "$TEST_ROOT/failed-evidence" "$TEST_ROOT/passed-evidence"

cleanup() {
  rm -f -- "$TEST_ROOT/docker" "$TEST_ROOT"/*.output "$TEST_ROOT"/*.calls \
    "$TEST_ROOT"/failed-evidence/* "$TEST_ROOT"/passed-evidence/*
  rmdir -- "$TEST_ROOT/failed-evidence" "$TEST_ROOT/passed-evidence" "$TEST_ROOT"
}
trap cleanup EXIT

if ! command -v python3 >/dev/null 2>&1; then
  echo 'python3 is required for the smoke matrix regression test.' >&2
  exit 2
fi

cat > "$TEST_ROOT/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$DOCKER_CALL_LOG"
printf '%s\n' '{"attempted":1,"passed":1,"failed":0,"skipped":0,"targets":[{"status":"passed"}]}'
exit "${FAKE_DOCKER_EXIT_CODE:-0}"
MOCK
chmod +x "$TEST_ROOT/docker"
export PATH="$TEST_ROOT:$PATH"

run_matrix() {
  local name="$1" fake_exit_code="$2" expected_exit_code="$3"
  local output_dir="$TEST_ROOT/${name}-evidence"
  local command_log="$TEST_ROOT/${name}.calls"
  local actual_exit_code=0

  if DOCKER_CALL_LOG="$command_log" FAKE_DOCKER_EXIT_CODE="$fake_exit_code" \
    GPU_SMOKE_OUTPUT_DIR="$output_dir" GPU_SMOKE_IMAGE=matrix-test-image \
    bash "$REPO_ROOT/scripts/docker/smoke-gpu-matrix.sh" \
    > "$TEST_ROOT/${name}.output" 2>&1; then
    actual_exit_code=0
  else
    actual_exit_code=$?
  fi

  test "$actual_exit_code" -eq "$expected_exit_code"
  test "$(wc -l < "$command_log")" -eq 12
  test "$(find "$output_dir" -maxdepth 1 -name '*.exitcode' | wc -l)" -eq 12
}

run_matrix failed 139 1
test "$(grep -c '"status": "failed"' "$TEST_ROOT/failed-evidence/matrix-summary.json")" -eq 12
test "$(grep -c '"exitCode": 139' "$TEST_ROOT/failed-evidence/matrix-summary.json")" -eq 12
grep -q 'Container exited with code 139' "$TEST_ROOT/failed-evidence/matrix-summary.json"

run_matrix passed 0 0
test "$(grep -c '"status": "passed"' "$TEST_ROOT/passed-evidence/matrix-summary.json")" -eq 12
test "$(grep -c '"exitCode": 0' "$TEST_ROOT/passed-evidence/matrix-summary.json")" -eq 12

echo 'Smoke matrix exit-code regression checks passed (12 failures and 12 passes).'
