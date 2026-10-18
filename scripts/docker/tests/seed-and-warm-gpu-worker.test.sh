#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../../.." && pwd)"
TEST_ROOT="$(mktemp -d)"
trap 'rm -f -- "$TEST_ROOT/docker" "$TEST_ROOT/nvidia-smi" "$TEST_ROOT/models.txt" "$TEST_ROOT/warm.txt" "$TEST_ROOT/calls" "$TEST_ROOT/output"; rmdir -- "$TEST_ROOT"' EXIT

# Run the real provisioning scripts with inert commands. No Docker daemon or GPU is used.
cat > "$TEST_ROOT/docker" <<'MOCK'
#!/usr/bin/env bash
printf '%s|%s\n' "${TRACKDUB_GPU_TARGET:-unset}" "$*" >> "$TEST_LOG"
MOCK
printf '#!/usr/bin/env bash\nexit 0\n' > "$TEST_ROOT/nvidia-smi"
chmod +x "$TEST_ROOT/docker" "$TEST_ROOT/nvidia-smi"
printf 'fixture-model\n' > "$TEST_ROOT/models.txt"
printf 'fixture.onnx\n' > "$TEST_ROOT/warm.txt"
export PATH="$TEST_ROOT:$PATH"
export TEST_LOG="$TEST_ROOT/calls"
export TRACKDUB_MODEL_SET="$TEST_ROOT/models.txt"
export TRACKDUB_WARM_MODEL_SET="$TEST_ROOT/warm.txt"
export TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE=1
unset TRACKDUB_COMPOSE_OVERRIDE

run_case() {
  local profile="$1" target="$2" expected="$3"
  : > "$TEST_LOG"
  TRACKDUB_GPU_PROFILE="$profile" TRACKDUB_GPU_TARGET="$target" \
    bash "$REPO_ROOT/scripts/docker/seed-and-warm-gpu-worker.sh" > "$TEST_ROOT/output" 2>&1
  test -s "$TEST_LOG"
  if grep -v "^$expected|" "$TEST_LOG"; then
    echo "Compose target did not follow selected profile: $profile / $target" >&2
    exit 1
  fi
  grep -q 'models download fixture-model' "$TEST_LOG"
  if [[ "$expected" == server ]]; then
    grep -q 'cache warm --provider tensorrt' "$TEST_LOG"
    ! grep -q 'providers trt-rtx install' "$TEST_LOG"
  else
    grep -q 'providers trt-rtx install' "$TEST_LOG"
  fi
}

run_case server '' server
run_case '' server server
run_case rtx rtx rtx
run_case '' '' rtx

: > "$TEST_LOG"
if TRACKDUB_GPU_PROFILE=server TRACKDUB_GPU_TARGET=rtx \
  bash "$REPO_ROOT/scripts/docker/seed-and-warm-gpu-worker.sh" > "$TEST_ROOT/output" 2>&1; then
  echo 'Conflicting profile and target should fail before provisioning.' >&2
  exit 1
else
  test "$?" -eq 2
fi
test ! -s "$TEST_LOG"
grep -q 'does not match' "$TEST_ROOT/output"
echo 'Provisioning profile/target regression checks passed (5 cases).'
