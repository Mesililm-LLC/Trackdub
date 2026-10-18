# Cloud GPU container: publish, seed, and warm

Trackdub's Linux GPU worker is a one-shot CLI container. The image contains the application/runtime; model weights, the engine cache, and LinML provider downloads live in persistent Docker volumes. Do not copy TensorRT engine cache files between GPU families, GPU models, or driver versions.

## Published images

One `Dockerfile` publishes two GPU profiles from a shared `runtime-base` stage: `--target rtx` (`TRACKDUB_GPU_PROFILE=rtx`, the TensorRT-RTX route) and `--target server` (`TRACKDUB_GPU_PROFILE=server`, strict classic TensorRT + CUDA). Neither profile bakes a VRAM budget; the session pool auto-scales its admission budget from detected GPU VRAM.

`.github/workflows/container-image.yml` builds `linux/amd64` for both profiles on container-related pull requests, checks the runtime, and publishes on `main`, `container-v*` tags, or manual dispatch. Every published tag carries the profile suffix:

```text
ghcr.io/trackdubllc/trackdub:main-rtx
ghcr.io/trackdubllc/trackdub:main-server
ghcr.io/trackdubllc/trackdub:sha-<commit>-rtx
ghcr.io/trackdubllc/trackdub:sha-<commit>-server
ghcr.io/trackdubllc/trackdub:container-vX.Y.Z-rtx
ghcr.io/trackdubllc/trackdub:container-vX.Y.Z-server
ghcr.io/trackdubllc/trackdub:pr-N-rtx
ghcr.io/trackdubllc/trackdub:pr-N-server
ghcr.io/trackdubllc/trackdub:latest-rtx
ghcr.io/trackdubllc/trackdub:latest-server
```

The earlier bare tags (`main`, `sha-<commit>`, `container-vX.Y.Z`, `latest`) are retired deliberately: with two profiles, an unsuffixed tag would not say which GPU route the image provisions for. Local Compose builds pick a target with `TRACKDUB_GPU_TARGET` (default `rtx`); `docker-compose.aws.yml` always selects the `server` target and defaults `TRACKDUB_IMAGE` to `ghcr.io/trackdubllc/trackdub:main-server` so AWS workers pull the published server image.

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
- The classic TensorRT engine cache is configured under `TRACKDUB_ENGINE_CACHE_ROOT` at `classic-trt/<gpu-arch|driver|trt-version|ort-version>`. The live 2026-10-18 acceptance below did **not** verify persistence: the warm and smoke sessions produced no classic engine files, so do not assume cross-process reuse until the blocker is fixed and this test passes.

## Two-profile and engine-cache live verification (2026-10-18)

Live verification ran on the local Windows Docker Desktop host with an NVIDIA GeForce RTX 5070 (12,227 MiB, driver 617.42), TensorRT 10.13.3, and ORT 1.31.0. At initial `nvidia-smi` inspection the GPU was at 0% utilization with 2,944 MiB in use; unrelated containers were running, so CPU contention may have affected wall-clock measurements. The profiles were rebuilt locally during diagnosis; the final diagnostic image IDs were `74cfa3a671776403a69fbc5c898f474482a55e54b6` (`trackdub-gpu:rtx`) and `919fc0690f5fd83582a47c0329eb7bfff4c1cc30dadd8973ada3e1f53d5b8d71` (`trackdub-gpu:server`). The working-tree diagnostics were discarded after the acceptance still failed; the image tags used for the measurements include those temporary diagnostic changes and are not a clean build of the final committed source.

Both image tags reported `Cuda available=true` and `TensorRt available=true` with `--gpus all`. `TensorRTRtx available=false` on both bare `docker run` probes because the LinML plugin volume is not attached; this is expected. The requested DeepFilterNet3 and Parakeet model files were present in `trackdub_trackdub-data`. Before warming, `/data/engine-cache` did not exist; existing TensorRT-RTX engine files were instead under `/data/models/EngineCache`, outside the classic cache root. No classic cache directory was present to delete.

Classic TensorRT cache warm was attempted against DeepFilterNet3 encoder, Parakeet encoder, and Parakeet joint decoder, with one container per pass. The first pass took 53.036 s and returned `compiled=0, reused=0, failed=3`; each item reported `status=failed` because its session created no files in `/data/engine-cache/classic-trt/Blackwell-617.42-trt-10.13.3-ort-1.31.0`. Item warm-load times were 10,880.8 ms, 6,250.3 ms, and 2,264.0 ms. The identical second pass took 20.533 s and again returned `compiled=0, reused=0, failed=3` (4,575.8 ms, 1,618.4 ms, and 1,336.1 ms per item). A post-pass file listing found zero files under `/data/engine-cache/classic-trt`; thus this was not compiled-then-reused acceptance. The initial volume had no classic cache artifacts, so the failure was not caused by a stale classic cache.

MADLAD `translation-madlad-trt-fp16` was smoked twice on the server image with `TRACKDUB_SESSION_VRAM_BUDGET_MB=14000`. Both JSON verdicts were `passed=1, failed=0`, exit 0. The first wall-clock was 1,034.925 s (17m 14.925s); the second was 1,112.843 s (18m 32.843s), 7.53% slower rather than faster. No classic TensorRT engine files appeared in the cache root after the first smoke. The successful inference verdict therefore does not establish cache reuse. The 14,000 MB override is only a local 12 GiB admission workaround; auto-scaled admission for MADLAD on 24/48 GiB G6/G6e cards remains unverified.

The requested matrix rerun used `GPU_SMOKE_IMAGE=trackdub-gpu:rtx` and completed in 696.746 s with exit 0. The current script emitted **12 cells** (7 CUDA and 5 TensorRT), not 13: 8 passed and 4 failed. Parakeet, SortFormer diarization, DeepFilterNet3, Kokoro CUDA, and CosyVoice CUDA passed. Both Spleeter cells failed with the known dummy-input dimension mismatch (`x`, index 1: got 1, expected 2); both MADLAD cells were rejected before session creation because the bundle estimate was 12,758 MB versus the auto-scaled 9,170 MB admission budget. This matrix run did not include a CosyVoice TensorRT cell. Full machine-readable results and per-cell stdout/stderr/exit codes are in `matrix/matrix-summary.json` and its sibling files in the evidence directory noted below.

Raw command output, stderr, exit codes, wall-clock timings, image/provider probes, and cache file listings were saved under `C:/Users/tonyt/AppData/Local/hermes/cache/scratch/engine-cache-evidence-2026-10-18/`. The evidence confirms the classic TensorRT cache persistence/reuse path is currently blocked; provisioning must not be treated as verified until the warm runs create artifacts and the second MADLAD smoke reuses them.

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

Provisioning branches on `TRACKDUB_GPU_PROFILE` (default `rtx`), which each published image carries as an environment variable; the same script provisions either profile, with the seed step shared.

RTX profile (local / TensorRT-RTX): install the NVIDIA driver and NVIDIA Container Toolkit on the host first. Review the NVIDIA TensorRT-RTX and CUDA license terms, then accept them explicitly for this worker:

```bash
export TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE=1
./scripts/docker/seed-and-warm-gpu-worker.sh
```

The script checks for a host-visible NVIDIA GPU, seeds the curated model volume, installs the LinML TensorRT-RTX plugin into the dedicated Compose volume, and runs `trackdub cache warm` against only `docker/poc-warm-models.txt`. This explicit list avoids recursively warming every `.onnx` in the volume: a full scan hit an OPUS-MT decoder that can terminate the host with a native stack overflow, and the Silero VAD graph fails TensorRT import on its `If`/`Squeeze` branches. A failed target stops provisioning; unsupported models are deliberately excluded and listed in comments. Parakeet TDT is in the TRT warm allowlist; the small Silero VAD model intentionally runs on CPU. The NVIDIA acceptance variable is deliberately not defaulted in scripts or CI.

Server profile (AWS / strict classic TensorRT + CUDA): no TensorRT-RTX license acceptance and no plugin install. The server image and the AWS Compose override select this profile; the script seeds the same model volume, then warms classic TensorRT engines with `trackdub cache warm --provider tensorrt` against `docker/poc-warm-models.server.txt`. A warm-list override applies to either profile via `TRACKDUB_WARM_MODEL_SET`.

For AWS, use a dedicated EBS volume mounted on the host at `/mnt/trackdub-data` so weights and GPU-specific engine artifacts survive container replacement. Format/attach/mount it through your normal EC2 provisioning, make the mount persistent, and grant the container's UID/GID 10001 write access:

```bash
sudo mkdir -p /mnt/trackdub-data
sudo chown 10001:10001 /mnt/trackdub-data
export TRACKDUB_COMPOSE_OVERRIDE=docker-compose.aws.yml
# Server profile: no TRACKDUB_ACCEPT_NVIDIA_TENSORRT_RTX_LICENSE is needed.
TRACKDUB_GPU_PROFILE=server ./scripts/docker/seed-and-warm-gpu-worker.sh
```

`docker-compose.aws.yml` defaults to the published `ghcr.io/trackdubllc/trackdub:main-server` image (override with `TRACKDUB_IMAGE`) and builds the `server` target when Compose must build instead of pull. The AWS override binds that EBS mount to `/data`; the seed script detects the override and uses it for every CLI invocation. No VRAM budget is pinned anymore: the session pool auto-scales its admission budget from detected GPU VRAM (75% on cards up to 16 GiB, 85% above that, floor 4096 MB), detecting VRAM via /proc and sysfs with an NVML fallback for containers where /proc/driver/nvidia is hidden. Run `trackdub doctor` and check the `accelerator-vram-budget` row to see the detected VRAM, the resolved budget, and its source; it warns when detection failed and the floor fallback is in effect. Set `TRACKDUB_SESSION_VRAM_BUDGET_MB` only as an explicit operator override. MADLAD's TRT-RTX bundle is estimated at ~8069 MB for its encoder/decoder sessions. Treat the resulting engine cache as specific to that instance's GPU and driver.

Run the provisioning script once on each newly provisioned GPU/driver combination, and again after a driver or provider update. It writes the cache under `/data` (the persistent `trackdub-data` volume). The separate `trackdub-linml` volume persists plugin downloads. If changing the Compose project name, carry both volumes forward or the worker will appear unseeded.

For CPU-only local use, run only `seed-model-volume.sh`; do not run the GPU warm script. The CI workflow intentionally does not download weights or accept NVIDIA licenses. Its `doctor` check allows the empty model-volume readiness failure but requires the manifest, ffmpeg/ffprobe, espeak-ng, and writable model cache checks to pass.
