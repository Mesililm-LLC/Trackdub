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

MADLAD-400's manifest-pinned `trt-fp16` variant passed the same provider smoke (including its production translation probe) with `TRACKDUB_SESSION_VRAM_BUDGET_MB=9000` on the local 12 GiB RTX 5070: 1 passed, 0 failed, process exit 0. Without the explicit budget it was rejected by the 4096 MB floor before session creation. The AWS G6/G6e override sets 16384 MB as described below.

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
| CUDA | translation-madlad-trt-fp16 | Failed | VRAM admission rejected the bundle before session creation: the session bundle needs about 12758 MB across its graphs, which exceeds the 9000 MB local admission budget. This route needs a 24 GiB-class GPU; the AWS override defaults the budget to 16384 MB. TensorRT-RTX remains the local MADLAD route, with a bundle estimate of about 8069 MB. |
| CUDA | tts-kokoro | Passed | Kokoro session and smoke inference passed. |
| CUDA | tts-cosyvoice-int8 | Passed | CosyVoice `int8` session and smoke inference passed. |
| TensorRT | diarization | Passed | SortFormer diarization session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | enhancement-deepfilternet3 | Passed | DeepFilterNet3 session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | asr-parakeet-tdt-0.6b-v3 | Passed | Parakeet TDT session and smoke inference passed on the native TensorRT execution provider. |
| TensorRT | separation-spleeter | Failed | Same smoke dummy-input dimension mismatch as CUDA (input `x` dimension index 1 got 1, expected 2). This is the same harness limitation, not a TensorRT-specific failure, and the production Spleeter entry path is unaffected. |
| TensorRT | translation-madlad-trt-fp16 | Failed (policy-excluded) | Production policy excludes the `madlad` engine family from native TensorRT because encoder-decoder session construction terminates the host process with a native stack overflow. TensorRT-RTX remains the MADLAD route. |
| TensorRT | tts-cosyvoice-int8 | Failed | The TensorRT execution provider failed to create an engine from the CosyVoice `int8` network. The int8 graphs are not TensorRT-compilable, consistent with the earlier TensorRT-RTX Myelin rejection of their int8 dtype. CUDA is the CosyVoice route. |

Conclusions from this pass:

- The tentpole routes are unchanged: Parakeet works on any GPU execution provider tested, MADLAD `trt-fp16` runs via TensorRT-RTX, Kokoro runs on CUDA, and CosyVoice `int8` runs on CUDA.
- Native TensorRT in the image is a working fallback execution provider for Parakeet, SortFormer, and DeepFilterNet3. That duplicates the existing TensorRT-RTX coverage for those models, so there is no production need to re-rank provider preferences for them.
- No planner or catalog changes were made from this pass. The CUDA passes confirm the existing Parakeet, Kokoro, and CosyVoice routes, and the Spleeter and MADLAD outcomes are explained by a smoke harness limitation, VRAM admission on a 12 GiB card, and existing policy, not by planner defects.
- Native TensorRT engine caches are not persisted by Trackdub today: the native TensorRT provider selection configures no engine cache path, so engines rebuild per process until a cache directory is configured and warmed per worker.

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

The AWS override binds that EBS mount to `/data`; the seed script detects the override and uses it for every CLI invocation. For the planned AWS G6/G6e workers, it also defaults `TRACKDUB_SESSION_VRAM_BUDGET_MB` to 16384 MB because the Linux `/proc` VRAM probe may not be visible in a container. Set it explicitly lower on smaller GPUs; do not use the 16 GiB default on the 12 GiB local RTX 5070. MADLAD's TRT-RTX bundle is estimated at ~8069 MB for its encoder/decoder sessions. Treat the resulting engine cache as specific to that instance's GPU and driver.

Run the provisioning script once on each newly provisioned GPU/driver combination, and again after a driver or provider update. It writes the cache under `/data` (the persistent `trackdub-data` volume). The separate `trackdub-linml` volume persists plugin downloads. If changing the Compose project name, carry both volumes forward or the worker will appear unseeded.

For CPU-only local use, run only `seed-model-volume.sh`; do not run the GPU warm script. The CI workflow intentionally does not download weights or accept NVIDIA licenses. Its `doctor` check allows the empty model-volume readiness failure but requires the manifest, ffmpeg/ffprobe, espeak-ng, and writable model cache checks to pass.
