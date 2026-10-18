#!/usr/bin/env bash
set -euo pipefail
#
# smoke-gpu-matrix.sh — run the CUDA and full-TensorRT smoke matrix for the
# POC tentpole models and write machine-readable evidence per run.
#
# Purpose:
#   Each model runs in its own `docker run` so a native crash in one smoke
#   process cannot abort or contaminate the rest of the matrix. Every run's
#   stdout, stderr, and exit code are captured as separate evidence files.
#
# Prerequisites:
#   - The container image is built (default: container-pipeline-trackdub:latest).
#   - Models are seeded in the Docker volume `trackdub_trackdub-data`
#     (see scripts/docker/seed-model-volume.sh), mounted at /data.
#   - An NVIDIA GPU with a working driver / NVIDIA Container Toolkit
#     (`docker run --gpus all` must work).
#
# Usage:
#   scripts/docker/smoke-gpu-matrix.sh [--dry-run]
#
# Environment overrides:
#   GPU_SMOKE_OUTPUT_DIR       Evidence directory
#                              (default: $(pwd)/gpu-smoke-evidence).
#   GPU_SMOKE_VRAM_BUDGET_MB   Value passed as TRACKDUB_SESSION_VRAM_BUDGET_MB
#                              to each smoke run. Unset by default: the pool
#                              auto-scales from detected VRAM. Set it only to
#                              reproduce a specific admission budget.
#   GPU_SMOKE_IMAGE            Container image to run
#                              (default: container-pipeline-trackdub:latest).
#
# Evidence / result of record:
#   The JSON summary printed on stdout by each smoke run is the result of
#   record. Native smoke processes can exit 139 (SIGSEGV) during teardown
#   AFTER printing their JSON summary, so a non-zero exit code alone does
#   not mean the smoke failed; the script records the exit code separately
#   and never aborts the matrix on a non-zero run.
#
# Matrix notes:
#   Silero VAD is excluded entirely (CPU by design). tts-kokoro is excluded
#   from the TensorRT run (planner policy excludes Kokoro from TensorRT)
#   but is included in the CUDA run.

PROJECT="${COMPOSE_PROJECT_NAME:-trackdub}"
IMAGE="${GPU_SMOKE_IMAGE:-${PROJECT}-trackdub:latest}"
VRAM_BUDGET_MB="${GPU_SMOKE_VRAM_BUDGET_MB:-}"
# Only forward the budget when explicitly set; otherwise the container's
# session pool auto-scales from detected VRAM.
VRAM_ENV_ARGS=()
if [[ -n "$VRAM_BUDGET_MB" ]]; then
  VRAM_ENV_ARGS=(-e "TRACKDUB_SESSION_VRAM_BUDGET_MB=$VRAM_BUDGET_MB")
fi
OUTPUT_DIR="${GPU_SMOKE_OUTPUT_DIR:-$(pwd)/gpu-smoke-evidence}"
if command -v cygpath >/dev/null 2>&1; then
  OUTPUT_DIR="$(cygpath -m "$OUTPUT_DIR")"
fi
VOLUME="${PROJECT}_trackdub-data"

DRY_RUN=0
if [[ "${1:-}" == "--dry-run" ]]; then
  DRY_RUN=1
elif [[ $# -gt 0 ]]; then
  echo "Unknown argument: $1 (expected --dry-run or no arguments)" >&2
  exit 2
fi

CUDA_LABELS=(
  diarization
  enhancement-deepfilternet3
  asr-parakeet-tdt-0.6b-v3
  translation-madlad-trt-fp16
  separation-spleeter
  tts-kokoro
  tts-cosyvoice-int8
)

# Same as CUDA minus tts-kokoro (planner policy excludes Kokoro from
# TensorRT) and minus tts-cosyvoice-int8 (int8 graphs are not
# TensorRT-compilable; CosyVoice int8 runs on the CUDA route).
TENSORRT_LABELS=(
  diarization
  enhancement-deepfilternet3
  asr-parakeet-tdt-0.6b-v3
  translation-madlad-trt-fp16
  separation-spleeter
)

echo "GPU smoke matrix output directory: $OUTPUT_DIR"
echo "Image: $IMAGE"
echo "VRAM budget (TRACKDUB_SESSION_VRAM_BUDGET_MB): ${VRAM_BUDGET_MB:-auto (detected VRAM)}"

if [[ "$DRY_RUN" -eq 1 ]]; then
  for provider in cuda tensorrt; do
    if [[ "$provider" == "cuda" ]]; then
      labels=("${CUDA_LABELS[@]}")
    else
      labels=("${TENSORRT_LABELS[@]}")
    fi
    for label in "${labels[@]}"; do
      echo "[dry-run] docker run --rm --gpus all -v $VOLUME:/data ${VRAM_ENV_ARGS[*]} $IMAGE providers $provider smoke --model $label"
    done
  done
  echo "Dry run complete. No containers were run. Output directory: $OUTPUT_DIR"
  exit 0
fi

mkdir -p "$OUTPUT_DIR"

run_smoke() {
  local provider="$1"
  local label="$2"
  local stdout_file="$OUTPUT_DIR/${provider}__${label}.stdout.json"
  local stderr_file="$OUTPUT_DIR/${provider}__${label}.stderr.log"
  local exitcode_file="$OUTPUT_DIR/${provider}__${label}.exitcode"
  local exit_code=0

  set +e
  docker run --rm --gpus all \
    -v "$VOLUME:/data" \
    "${VRAM_ENV_ARGS[@]}" \
    "$IMAGE" \
    providers "$provider" smoke --model "$label" \
    >"$stdout_file" 2>"$stderr_file"
  exit_code=$?
  set -e

  printf '%s\n' "$exit_code" >"$exitcode_file"
  echo "$provider $label exit=$exit_code"
}

for label in "${CUDA_LABELS[@]}"; do
  run_smoke cuda "$label"
done

for label in "${TENSORRT_LABELS[@]}"; do
  run_smoke tensorrt "$label"
done

# Aggregation: the per-run JSON on stdout is the result of record; the exit
# code is reported alongside it, not used as the pass/fail verdict.
if command -v python3 >/dev/null 2>&1; then
  OUTPUT_DIR="$OUTPUT_DIR" python3 - <<'PYEOF'
import glob
import json
import os

output_dir = os.environ["OUTPUT_DIR"]
rows = []

for stdout_path in sorted(glob.glob(os.path.join(output_dir, "*.stdout.json"))):
    base = os.path.basename(stdout_path)
    stem = base[: -len(".stdout.json")]
    provider, _, label = stem.partition("__")

    exit_code = None
    exitcode_path = os.path.join(output_dir, stem + ".exitcode")
    try:
        with open(exitcode_path, encoding="utf-8") as fh:
            exit_code = int(fh.read().strip())
    except (OSError, ValueError):
        exit_code = None

    # Be defensive: scan lines from the end for the last JSON object line.
    summary = None
    try:
        with open(stdout_path, encoding="utf-8", errors="replace") as fh:
            lines = fh.read().splitlines()
    except OSError:
        lines = []
    for line in reversed(lines):
        line = line.strip()
        if line.startswith("{"):
            try:
                summary = json.loads(line)
            except json.JSONDecodeError:
                summary = None
            break

    if not isinstance(summary, dict):
        rows.append({
            "provider": provider,
            "label": label,
            "status": "crashed-no-json",
            "attempted": 0,
            "passed": 0,
            "failed": 0,
            "skipped": 0,
            "detail": None,
            "exitCode": exit_code,
        })
        continue

    attempted = int(summary.get("attempted") or 0)
    passed = int(summary.get("passed") or 0)
    failed = int(summary.get("failed") or 0)
    skipped = int(summary.get("skipped") or 0)

    detail = None
    for target in summary.get("targets") or []:
        if isinstance(target, dict) and target.get("status") == "failed":
            detail = target.get("detail")
            break

    if failed >= 1:
        status = "failed"
    elif attempted == 0:
        status = "skipped"
    elif passed == 1 and failed == 0:
        status = "passed"
    elif passed == 0 and skipped >= 1:
        status = "skipped"
    else:
        status = "failed"

    rows.append({
        "provider": provider,
        "label": label,
        "status": status,
        "attempted": attempted,
        "passed": passed,
        "failed": failed,
        "skipped": skipped,
        "detail": detail,
        "exitCode": exit_code,
    })

rows.sort(key=lambda r: (r["provider"], r["label"]))

summary_path = os.path.join(output_dir, "matrix-summary.json")
with open(summary_path, "w", encoding="utf-8") as fh:
    json.dump(rows, fh, indent=2)
    fh.write("\n")

print(f"{'provider':<10} {'label':<32} status")
for row in rows:
    print(f"{row['provider']:<10} {row['label']:<32} {row['status']}")
PYEOF
else
  echo "python3 not found; skipping aggregation (per-run evidence files are in $OUTPUT_DIR)."
fi

echo "GPU smoke matrix complete. Evidence: $OUTPUT_DIR"
