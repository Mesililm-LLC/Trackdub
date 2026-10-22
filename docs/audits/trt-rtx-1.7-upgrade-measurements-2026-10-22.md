# TensorRT-RTX 1.7 upgrade measurements (2026-10-22)

Plan: `docs/plans/HANDOFF-trt-rtx-1.7-ep-abi-0.5.0-upgrade.md`, Part 1.7 and 1.9.

## Setup

- Machine: NVIDIA GeForce RTX 5070 (Blackwell, 12 GB), driver 617.42 (`32.0.16.1742`), AMD Ryzen 7 5700X3D, Windows 11 26200.
- Baseline: commit `c59c1033` (EP ABI 0.4.2 / TensorRT-RTX 1.6.1), built in a separate worktree.
- Candidate: this branch (EP ABI 0.5.0 / TensorRT-RTX 1.7.1).
- Both builds run the Windows ML ORT flavor, **ORT 1.27.1** (`HostOrtRuntimeVersion`), inside EP ABI 0.5.0's tested range of 1.25–1.30.
- Graph benchmarks: `Trackdub.Benchmarks.DevHost --provider trt-rtx --artifact source --runtime-cache-dir <per model>`. "cold" uses an empty runtime cache, and "warm" is a second fresh process on the same cache.
- Linux was **not** measured. Linux installs TRT-RTX through LinML.Catalog, which still pins the 0.4.0 wheel (TensorRT-RTX 1.6.1), so a Linux run would only re-test 1.6.1.

## Steady-state latency (median of 3 interleaved rounds × 50 runs, warm runtime cache)

| Graph | p50 1.6.1 → 1.7.1 | p95 1.6.1 → 1.7.1 | Load, fresh process |
|---|---|---|---|
| MADLAD-400 3B encoder (`trt_rtx_mixed_fp16_fp32`) | 18.5 → 6.4 ms (**−66%**) | 21.8 → 9.5 ms | 17.0 → 20.6 s (**+22%**, engine build) |
| DeepFilterNet3 `enc` | 1.27 → 0.90 ms (−29%) | 2.0 → 1.3 ms | 0.80 → 0.64 s (−20%) |
| Whisper tiny decoder | 4.3 → 3.3 ms (−22%) | 7.6 → 4.0 ms | 3.7 → 3.3 s (−10%) |
| CosyVoice `llm/token_generator` | 6.9 → 5.6 ms (−19%) | 12.1 → 7.9 ms | 10.7 → 10.3 s (−4%) |
| Parakeet TDT `decoder_joint` | ≈1 ms either way (noise) | ≈1.3 ms | 1.0 → 0.82 s (−18%) |

An earlier single-run pass showed Whisper tiny decoder (+47%) and CosyVoice token generator (+136%) regressions. The interleaved repeats showed both were noise.

## Cold source-graph load (single run, empty runtime cache)

| Graph | 1.6.1 | 1.7.1 |
|---|---|---|
| DeepFilterNet3 `enc` | 4.8 s | 2.6 s |
| Parakeet encoder | 53.5 s | 42.3 s |
| Parakeet `decoder_joint` | 2.7 s | 2.0 s |
| MADLAD encoder | 25.4 s | 22.2 s |
| CosyVoice flow estimator | 80.1 s | 55.5 s |
| sortformer (run fails, see below) | 43.3 s | 32.2 s |

## EP-context (`cache warm`) and product paths

| Step | 1.6.1 | 1.7.1 |
|---|---|---|
| `cache warm` compile, sortformer | 53.8 s | 38.1 s |
| `cache warm` compile, Parakeet encoder | 55.4 s | 40.4 s |
| `cache warm` compile, Parakeet `decoder_joint` | 1.3 s | 1.2 s |
| `cache warm` result, 5 POC graphs | compiled 3 + reused 2, 0 failed | compiled 5, 0 failed |
| MADLAD `providers trt-rtx smoke --model translation-madlad-trt-fp16` after `cache warm` (2 runs) | 29.8 / 29.0 s | 23.7 / 21.7 s (**−23%**) |
| Parakeet ASR stage, fresh process, 2nd run: `phase:onnx-session-create` | 9.2 s | 7.9 s |

The first load right after `cache warm` was slower on 1.7.1 (sortformer 2.3 → 7.2 s, Parakeet encoder 4.1 → 6.2 s). The baseline was reading an already-populated flat runtime cache, while 1.7.1 started from its new, empty `EngineCache/trt-rtx/<fingerprint>` directory. Once both caches are warm, 1.7.1 is faster (session-create row above).

## Exclusion retests (plan 1.7)

| Model | 1.6.1 | 1.7.1 | Decision |
|---|---|---|---|
| silero-vad | engine build fails: `squeeze index (1) must be less than length (0)` in `If` else branch | still fails: same `Squeeze` error in both branches, plus `graphScope.cpp` scope assertion | **keep TRT exclusion** |
| Kokoro-82M (`model.onnx`) | Myelin `buildSerializedNetwork` shape-constraint error | same Myelin error | **keep CUDA/CPU-only policy** |
| sortformer (synthetic input) | builds, run fails `ShapeInferenceNotRegistered` in `IExecutionContext` | same | no change; real-audio path not measured here |
| Whisper large-v3 decoder | builds and runs (33.0 ms p50) | builds and runs (30.4 ms p50) | policy unchanged (pair-level validation still needed) |
| Whisper tiny/small encoders | harness error: standalone encoder has no decoder to pair with | same | not a TRT result |
| CosyVoice flow estimator / token generator | builds and runs | builds and runs, faster | policy unchanged (CosyVoice routes to CUDA) |

## Not measured / known gaps

- `controlled --stage translation --model madlad400 --provider TensorRTRtx` fails with a bare `STAGE_FAILED` on **both** builds (no reason in the report, log or project DB). This is a harness issue unrelated to the upgrade. The MADLAD smoke above stands in for it.
- CosyVoice `int8` graphs are not cached locally and were not retested.
- Linux / LinML route: blocked on a LinML.Catalog release that pins wheel `onnxruntime-ep-nv-tensorrt-rtx` 0.5.0 (published on PyPI).
- Driver ≤ R615 DirectML+TRT-RTX workaround: unit-tested only. No affected driver was available.
