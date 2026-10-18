# Cloud GPU container: publish, seed, and warm

Trackdub's Linux GPU worker is a one-shot CLI container. The image contains the application/runtime; model weights, the engine cache, and LinML provider downloads live in persistent Docker volumes. Do not copy TensorRT engine cache files between GPU families, GPU models, or driver versions.

## Published image

`.github/workflows/container-image.yml` builds `linux/amd64` on container-related pull requests, checks the runtime, and publishes on `main`, `container-v*` tags, or manual dispatch to:

```text
ghcr.io/trackdubllc/trackdub:main
ghcr.io/trackdubllc/trackdub:sha-<commit>
ghcr.io/trackdubllc/trackdub:container-vX.Y.Z
```

The first GHCR package may default to private. After the first successful publish, set `ghcr.io/trackdubllc/trackdub` visibility to public in the package settings if AWS workers should pull it anonymously. ECR mirroring can be added later without changing the model-volume process.

## Curated POC model volume

The initial seed list is `docker/poc-models.txt`, one pinned manifest model per standard pipeline stage for the proposal's EN→ES and EN→ZH test matrix:

- Silero VAD
- DeepFilterNet3 speech enhancement
- SortFormer 4-speaker diarization
- NVIDIA Parakeet TDT 0.6B v3 ASR (`tonythethompson/parakeet-tdt-0.6b-v3-onnx`)
- MADLAD-400 3B translation, explicitly seeded as the manifest variant `trt-fp16` (the supported TRT-RTX export, rather than the default quantized files)
- Spleeter source separation
- Kokoro 82M TTS (`onnx-community/Kokoro-82M-v1.0-ONNX`), the planner's first TTS preference
- CosyVoice 300M voice-cloning TTS, seeded as the `int8` variant (covers English, Spanish, and Chinese)

The set is intentionally curated, not every one of the 47 model entries or experimental variant in the developer cache. `scripts/docker/seed-model-volume.sh` downloads each exact model through Trackdub's CLI; the list supports `model-id|variant-alias` for pinned variants such as the `trt-fp16` translation export. The CLI resolves pinned revisions and SHA-256 hashes in `bundled-models.manifest.json`; an integrity mismatch fails instead of silently accepting different weights. The manifest marks all eight selected models as commercially usable and redistributable; seven require attribution. Preserve each model's license/attribution notice alongside the seeded weights. Silero VAD is intentionally CPU-side as a small model.

The runtime planner now places `parakeet-tdt-0.6b-v3` first in the ASR preference list and admits its `parakeet-tdt` engine family; the planner regression test confirms the EN POC resolves to this model when it is not yet cached. Parakeet passed `providers trt-rtx smoke --model parakeet-tdt-0.6b-v3` on an RTX 5070: 1 attempted, 1 passed, 0 failed, 0 skipped. It verified the selected provider and ran the encoder/decoder probe; the CLI then exited 139 during native teardown after printing the successful summary, so do not gate automation on that smoke command's exit code.

MADLAD-400's manifest-pinned `trt-fp16` variant passed the same provider smoke (including its production translation probe) with `TRACKDUB_SESSION_VRAM_BUDGET_MB=9000` on the local 12 GiB RTX 5070: 1 passed, 0 failed, process exit 0. Without the explicit budget it was rejected by the 4096 MB floor before session creation (that was before VRAM auto-scaling landed; see the per-worker section below).

The previous `Xenova/whisper-medium` choice is deliberately not seeded: production policy excludes `whisper-onnx` from TensorRT-RTX. Qwen3-ASR is manifest-marked for TRT-RTX but its production planner explicitly prefers DML/CUDA because the TRT graph smoke falls back on unsupported ops. Nemotron is not in the auto-planning shipping lane. Parakeet was chosen because this actual container smoke passed on TRT-RTX.

Native CUDA in the container was initially broken by packaging, not by the GPU: NuGet publish asset selection placed the CPU `Microsoft.ML.OnnxRuntime` core at `/app/libonnxruntime.so`, shadowing the CUDA-enabled core from the transitive `Microsoft.ML.OnnxRuntime.Gpu.Linux` package, so `CUDAExecutionProvider` could not register even though `libonnxruntime_providers_cuda.so` was present. The Dockerfile now overwrites the published core with the GPU Linux core in the build stage (container publish only; desktop publishes are unaffected). After the fix, `providers list` reports CUDA available, and `check --execution-provider cuda --require-execution-provider` planned every stage ready on the RTX 5070 (ASR Parakeet, translation MADLAD, TTS Kokoro) with real CUDA sessions created during planner smoke.

TTS routing follows from that fix. The planner restricts Kokoro to CUDA/CPU (TensorRT-RTX cannot infer its duration-dependent shapes), so Kokoro is the CUDA-verified TTS tentpole and the planner's first TTS preference. CosyVoice remains the voice-cloning dubbing model: its `int8` variant declares CUDA, but its quantized graphs are not TRT-RTX compilable (the Myelin builder rejects their int8 dtype, verified live), and its vocoder graph falls back to CPU, so CosyVoice runs on the CUDA route rather than TRT-RTX.

On an AWS Linux GPU worker with Docker Compose v2:

```bash
git clone --depth 1 https://github.com/trackdubllc/Trackdub.git
cd Trackdub

docker volume create trackdub_trackdub-data
./scripts/docker/seed-model-volume.sh
```

The Compose project name controls volume names. Use the same project name on every invocation (or set `COMPOSE_PROJECT_NAME`) so the `/data` volume is reused. The model files live at `/data/models` inside the container. Expected local cache sizes are only rough planning figures; allow extra volume space for downloaded artifacts, generated engine contexts, logs, and future revisions.

## CUDA and native TensorRT smoke matrix (2026-10-17)

On 2026-10-17, the full CUDA and native TensorRT smoke matrix ran against image `container-pipeline-trackdub:latest` on a local RTX 5070 (12 GiB). The image pins TensorRT 10.13.3.9. This pass tested the native `Cuda` and `TensorRt` execution providers in the image; it did not retest TensorRT-RTX beyond the earlier evidence above. A smoke pass means the production smoke tester created a session on the requested provider and ran its smoke inference. It does not mean a full pipeline end-to-end run.

To reproduce:

```bash
docker compose build
docker run --rm --gpus all container-pipeline-trackdub:latest providers list
GPU_SMOKE_OUTPUT_DIR=/path/to/gpu-smoke-evidence scripts/docker/smoke-gpu-matrix.sh
```

`providers list` must report both `Cuda` and `TensorRt` as available before running the matrix. The script runs one `docker run` per model so a native crash in one run cannot abort or contaminate the rest of the matrix, and it writes per-run stdout, stderr, and exit code files under `GPU_SMOKE_OUTPUT_DIR`. The JSON summary printed on stdout is the result of record: a native smoke process may exit 139 during teardown after printing its successful JSON summary, so do not judge a run by its exit code alone.

| Provider | Model | Result | Note |
|---|---|---|---|
| CUDA | diarization | Passed | SortFormer diarization session and smoke inference passed. |
| CUDA | enhancement-deepfilternet3 | Passed | DeepFilterNet3 session and smoke inference passed. |
| CUDA | asr-parakeet-tdt-0.6b-v3 | Passed | Parakeet TDT session and smoke inference passed. |
| CUDA | separation-spleeter | Failed | Smoke dummy-input dimension mismatch: input `x` dimension index 1 got 1, expected 2. Identical on both execution providers, so this is a smoke harness limitation, not a provider failure. The production Spleeter entry path is unaffected; its stems were cache-warmed successfully during provisioning. |
| CUDA | translation-madlad-trt-fp16 | Failed | VRAM admission rejected the bundle before session creation: the session bundle needs about 12758 MB across its graphs, which exceeds the 9000 MB local admission budget. This route needs a 24 GiB-class GPU, where the auto-scaled budget (85% of detected VRAM) admits it. TensorRT-RTX remains the local MADLAD route, with a bundle estimate of about 8069 MB. |
| CUDA | tts-kokoro | Passed | Kokoro session and smoke inference passed. |
| CUDA | tts-cosyvoice-int8 | Passed | CosyVoice `int8` session and smoke inference passed. |
| TensorRT | diarization | Passed | SortFormer diarization session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | enhancement-deepfilternet3 | Passed | DeepFilterNet3 session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | asr-parakeet-tdt-0.6b-v3 | Passed | Parakeet TDT session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | separation-spleeter | Failed | Same smoke dummy-input dimension mismatch as CUDA (input `x` dimension index 1 got 1, expected 2). This is the same harness limitation, not a TensorRT-specific failure, and the production Spleeter entry path is unaffected. |
| TensorRT | translation-madlad-trt-fp16 | Passed (after policy correction) | The matrix run itself was refused by a production policy guard, not by TensorRT. An isolated-process retest on 2026-10-18 (ORT 1.31, TensorRT 10.13, RTX 5070) then passed the same smoke with exit 0 after a roughly 9 minute engine build. The guard had codified an unproven assumption: TensorRT support is an export property, and the pinned `trt-fp16` export is TensorRT-qualified on both TensorRT providers. The policy now says so; the other MADLAD exports (quantized, merged-decoder, int4-kv) were never built for TensorRT and stay unqualified until a TensorRT-built export passes the same isolated retest. Locally this route also needs the VRAM admission budget raised (bundle estimate about 12758 MB, as on CUDA); TensorRT-RTX remains the preferred MADLAD route. |
| TensorRT | tts-cosyvoice-int8 | Failed | The TensorRT execution provider failed to create an engine from the CosyVoice `int8` network. The int8 graphs are not TensorRT-compilable, consistent with the earlier TensorRT-RTX Myelin rejection of their int8 dtype. CUDA is the CosyVoice route. |

Conclusions from this pass:

- The tentpole routes are unchanged: Parakeet works on any GPU execution provider tested, MADLAD `trt-fp16` runs via TensorRT-RTX (preferred) and now classic TensorRT, Kokoro runs on CUDA, and CosyVoice `int8` runs on CUDA.
- Native TensorRT in the image is a working fallback execution provider for Parakeet, SortFormer, and DeepFilterNet3. That duplicates the existing TensorRT-RTX coverage for those models, so there is no production need to re-rank provider preferences for them.
- One planner and policy change did follow from this pass, after the isolated MADLAD retest: the planner now ranks classic TensorRT second for MADLAD (after TensorRT-RTX, before CPU), and the smoke guard treats TensorRT qualification as an export property instead of a model-family exclusion. The Spleeter outcome is a smoke harness limitation and the CUDA MADLAD outcome is VRAM admission on a 12 GiB card, not planner defects.
- Native TensorRT engine caches are not persisted by Trackdub today: the native TensorRT provider selection configures no engine cache path, so engines rebuild per process until a cache directory is configured and warmed per worker.

## TensorRT qualification retests (2026-10-18)

Several provider exclusions in the planner and the smoke guard dated from an ORT 1.24.5 / Windows-era run and had never been retested on the current stack. On 2026-10-18 each unproven exclusion was retested in an isolated process (one container per model and provider, so a host-killing failure could not hide as a catchable error) on ORT 1.31, TensorRT 10.13, RTX 5070. Policy now follows the evidence: TensorRT support is a property of a specific export, qualified when a TensorRT-built export passes this isolated smoke.

| Export | Classic TensorRT | TensorRT-RTX | Policy now |
|---|---|---|---|
| OPUS-MT (merged-decoder) | Passed | Session construction aborted the host process (exit 134) | TensorRT-RTX excluded only; classic TensorRT allowed |
| Silero VAD | Failed catchably: shape inference rejected the `If` subgraph ("TensorRT input ... has no shape specified") | Importer rejects the `If`/`Squeeze` graph (issue #329) | Both TensorRT providers excluded until a shape-inferred export ships; the model is tiny, so CPU/DirectML costs nothing |
| Whisper medium (`Xenova/whisper-medium`, whisper-onnx) | Passed | Passed | TensorRT denial removed; whisper-onnx uses the default provider list and variants are smoke-gated individually |
| Qwen3-ASR (`tonythethompson/qwen3-asr-0.6b-onnx`) | Isolated process terminated during import (plugin ops the importer cannot resolve) | Historically fell back catchably after a long compile | Classic TensorRT excluded (the planner smokes in-process and must never attempt a host-killing provider); TensorRT-RTX stays reachable behind DirectML/CUDA |
| GenAI text refinement (`tonythethompson/Qwen2.5-1.5B-Instruct`) | ORT GenAI has no classic TensorRT device ("NvTensorRtRtx execution provider is not supported in this build") | NvTensorRtRtx device aborted the host process (exit 134) | Both TensorRT kinds refused for bundled GenAI exports; CPU/CUDA remain the GenAI routes |

The GenAI retest also exposed a container packaging defect: publish output shipped only `libonnxruntime-genai-cuda.so`, not the base `libonnxruntime-genai.so` the loader probes for, so no GenAI model could load in the image at all. The Dockerfile now copies the base library from the GenAI NuGet package, the same pattern as the GPU ONNX Runtime core overwrite.

One related question was evaluated and rejected: serving CosyVoice through TensorRT-LLM or vLLM. CosyVoice `int8` already runs on the CUDA execution provider in Trackdub; what fails is TensorRT engine building from the int8 ModelOpt graphs, which is an export problem. TensorRT-LLM and vLLM host PyTorch LLM weights in a separate Python runtime, which would break the single self-contained binary and container deployment model, and neither would accelerate the HiFT vocoder that stays on CPU either way. If TTS latency ever measures as the bottleneck, the in-architecture path is an fp16 TensorRT-qualified CosyVoice export, qualified by the same isolated retest.

## Per-worker provider install and engine warm

Install the NVIDIA driver and NVIDIA Container Toolkit on the EC2 host first. Review the NVIDIA TensorRT-RTX and CUDA license terms, then accept them explicitly for this worker:

```bash
export TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE=1
./scripts/docker/seed-and-warm-gpu-worker.sh
```

The script checks for a host-visible NVIDIA GPU, seeds the curated model volume, installs the LinML TensorRT-RTX plugin into the dedicated Compose volume, and runs `trackdub cache warm` against only `docker/poc-warm-models.txt`. This explicit list avoids recursively warming every `.onnx` in the volume: a full scan hit an OPUS-MT decoder that can terminate the host with a native stack overflow, and the Silero VAD graph fails TensorRT import on its `If`/`Squeeze` branches. A failed target stops provisioning; unsupported models are deliberately excluded and listed in comments. Parakeet TDT is in the TRT warm allowlist; the small Silero VAD model intentionally runs on CPU. The NVIDIA acceptance variable is deliberately not defaulted in scripts or CI.

For AWS, use a dedicated EBS volume mounted on the host at `/mnt/trackdub-data` so weights and GPU-specific engine artifacts survive container replacement. Format/attach/mount it through your normal EC2 provisioning, make the mount persistent, and grant the container's UID/GID 10001 write access:

```bash
sudo mkdir -p /mnt/trackdub-data
sudo chown 10001:10001 /mnt/trackdub-data
export TRACKDUB_COMPOSE_OVERRIDE=docker-compose.aws.yml
export TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE=1 # only after reviewing the license terms
./scripts/docker/seed-and-warm-gpu-worker.sh
```

The AWS override binds that EBS mount to `/data`; the seed script detects the override and uses it for every CLI invocation. No VRAM budget is pinned anymore: the session pool auto-scales its admission budget from detected GPU VRAM (75% on cards up to 16 GiB, 85% above that, floor 4096 MB), detecting VRAM via /proc and sysfs with an NVML fallback for containers where /proc/driver/nvidia is hidden. Run `trackdub doctor` and check the `accelerator-vram-budget` row to see the detected VRAM, the resolved budget, and its source; it warns when detection failed and the floor fallback is in effect. Set `TRACKDUB_SESSION_VRAM_BUDGET_MB` only as an explicit operator override. MADLAD's TRT-RTX bundle is estimated at ~8069 MB for its encoder/decoder sessions. Treat the resulting engine cache as specific to that instance's GPU and driver.

Run the provisioning script once on each newly provisioned GPU/driver combination, and again after a driver or provider update. It writes the cache under `/data` (the persistent `trackdub-data` volume). The separate `trackdub-linml` volume persists plugin downloads. If changing the Compose project name, carry both volumes forward or the worker will appear unseeded.

For CPU-only local use, run only `seed-model-volume.sh`; do not run the GPU warm script. The CI workflow intentionally does not download weights or accept NVIDIA licenses. Its `doctor` check allows the empty model-volume readiness failure but requires the manifest, ffmpeg/ffprobe, espeak-ng, and writable model cache checks to pass.
