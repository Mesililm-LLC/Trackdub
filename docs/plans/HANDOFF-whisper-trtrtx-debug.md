# HANDOFF — Whisper TRT-RTX hardware debug

Date: 2026-09-22 (PT) / 2026-09-23 (UTC)
Branch: `main` (work was started on `trtrtx-handoff` / PR #253, flips + fixes were committed to `main` as below)
Machine: Windows, NVIDIA GeForce RTX 5070 (driver 617.14), pwsh 7.6, `Trackdub.slnx`
Goal: validate whisper-onnx (tiny/base/small/medium/large-v3) on `NvTensorRTRTXExecutionProvider`, then run `Flip-WhisperOnnxTrtRtx.ps1`.

## Status: DO NOT FLIP. Olive 5/5, C# hardware 2/5.

| Size | Olive validate+stage | C# `WhisperOnnxTrtRtx_*` |
|---|---|---|
| tiny | PASS | **PASS** |
| base | PASS | **PASS** |
| small | PASS | FAIL — empty transcript on silence (test assert) |
| medium | PASS | FAIL — `InvalidOperationException: Sequence contains more than one element` |
| large-v3 | PASS | FAIL — TRT-RTX engine build failure (`ShapeInferenceNotRegistered`) |

`Flip-WhisperOnnxTrtRtx.ps1` is all-or-nothing (one `pass=true` file gates all 5 families). Do not run it until the three failures below are resolved or the flip is narrowed.

## Commits on `main` (all by tonythethompson, in order)

1. `a1ec41f` — Flip 1 manifest: `trt-rtx` + recipe bindings for sortformer + nemotron-asr (gated on their `pass=true` files; verified providers lists).
2. `d2e0c54` — Flip 1 test: nemotron-asr trt-rtx presence assertion. **REVIEW BEFORE PUSH: -60/+7, deletes the `RecipeBindingComponentMustBeADeclaredComponent` Theory** — likely PR-vs-main drift from running the flip on the `trtrtx-handoff` tree.
3. `155381a` — Whisper recipe/validator fix #1 (see "Fixed so far").
4. `98d2b70` — Whisper recipe fix #2 (`keep_io_types`) + removed all 5 `Skip`s in `WhisperOnnxTrtRtxValidationTests.cs`.

Working tree is clean. Nothing uncommitted.

## Fixed so far (Olive stage went 0/5 → 5/5)

1. **EP registration**: whisper recipes declared bare `"NvTensorRTRTXExecutionProvider"`; Olive requires the `(name, path)` pair. All 15 files (`resources/olive-recipes/{onnx-community-whisper-{tiny,base,small},Xenova-whisper-{medium,large-v3}}/NvTensorRtRtx/{encoder,decoder}_trtrtx_fp16.json`, `eval_latency.json`) now use `["NvTensorRTRTXExecutionProvider", "${TRT_RTX_EP_PATH}"]` (minimal 5+/1- hunks; passes blocks untouched).
2. **Validator**: `tools/olive/Validate-WhisperOnnxTrtRtx.ps1` now resolves the EP DLL (`TRACKDUB_TRT_RTX_EP_DIR` or `…\Providers\trt-rtx\0.3.0\cu12\win-x64`), prepends it to `PATH`, substitutes `${TRT_RTX_EP_PATH}`, and runs `eval_latency.json` through `Resolve-Recipe` (was raw `Copy-Item`).
3. **Dummy data**: `OrtSessionParamsTuning` had no data and whisper dims are symbolic (`batch_size`, `encoder_sequence_length / 2`) → `torch.ones` TypeError. Added `DummyDataContainer`s (mirroring sortformer's `sortformer_steady_state` pattern: `providers_list` + `data_config` on the pass AND on the latency metric — the metric one is what fixed the pre-pass baseline evaluation). Steady-state shapes verified against the ONNX graphs + `preprocessor_config.json`:

| Size | mels | H | encoder `input_features` (fp32) | decoder `input_ids` (int64) / `encoder_hidden_states` (fp32) |
|---|---|---|---|---|
| tiny | 80 | 384 | [1,80,3000] | [1,1] / [1,1500,384] |
| base | 80 | 512 | [1,80,3000] | [1,1] / [1,1500,512] |
| small | 80 | 768 | [1,80,3000] | [1,1] / [1,1500,768] |
| medium | 80 | 1024 | [1,80,3000] | [1,1] / [1,1500,1024] |
| large-v3 | 128 | 1280 | [1,128,3000] | [1,1] / [1,1500,1280] |

4. **`keep_io_types: true`** on all 10 fp16 passes (Nemotron/SortFormer both have it): without it the staged models expect fp16 inputs while `WhisperOnnxAudioTranscriptionEngine` feeds fp32 → `[ErrorCode:InvalidArgument] Tensor element data type discovered: Float metadata expected: Float16`.

## Open failures (C# hardware tests)

Repro for all: `dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~WhisperOnnxTrtRtx" -m:1`
Single: `... --filter "FullyQualifiedName~WhisperOnnxTrtRtx_<Tiny|Base|Small|Medium|LargeV3>Model" -m:1 [--no-build]`
Olive per size: `pwsh -NoProfile -File tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize <tiny|base|small|medium|large-v3> -SkipLatency`
Staged models: `build/whisper-<size>-onnx-trtrtx-validated/onnx/{encoder_model,decoder_model}.onnx`

### F1 — large-v3: TRT-RTX engine build fails
`Microsoft.ML.OnnxRuntime.OnnxRuntimeException : [ErrorCode:ShapeInferenceNotRegistered] [NvTensorRTRTX EP] Failed to create serialized engine for fused node: NvTensorRTRTXExecutionProvider_NvTensorRTRTXExecutionProvider_3054060037779069909_0_0`
Propagates via `WhisperOnnxAudioTranscriptionEngine.cs:392` (`RunWithRetry`, `InferenceRetryPolicy.cs:59`) ← `:215` `DetectTranscriptLanguageAsync` ← test `:90`.
Unknown whether the failing session is encoder or decoder — determine first (ORT verbose logging, or load each staged model alone via `trackdub providers trt-rtx verify --model Xenova/whisper-large-v3 --entry <file>`).
Hypotheses: fused-node pattern at 1280-wide dims unsupported by TRT-RTX 1.5 builder; missing optimization-profile shapes for the decoder loop; needs builder flags/workspace tweak in `OnnxExecutionSessionFactory.BuildTensorRtRtxOptions`.

### F2 — small: empty transcript on silence fails the assert
`Assert.All() Failure ... RecognizedTranscriptSegment { ... Text = , DetectedLanguage = ru ... }` at `WhisperOnnxTrtRtxValidationTests.cs:95` (`Assert.False(string.IsNullOrWhiteSpace(segment.Text))`).
Note: silence SHOULD transcribe to empty — tiny/base "pass" by hallucinating. First check whether this reproduces on CPU (provider-independent → the assertion, not TRT-RTX, is wrong). Do not "fix" by making small hallucinate.

### F3 — medium: `.Single()` throws
`System.InvalidOperationException : Sequence contains more than one element` at `WhisperOnnxAudioTranscriptionEngine.cs:394` in `TranscribeRegionAsync` (via `:215` ← test `:90`).
Read `:380–400` — likely assumes a single language/segment candidate. Check CPU behavior too; may be engine bug independent of provider.

## Suggested debug order
1. F2-isolation (cheap): run small silence smoke against CPU provider. If empty → fix the test assertion (e.g. assert session ran on TRT-RTX + valid segments, not non-empty text), not the model.
2. F3: inspect `:394` context; reproduce on CPU; fix engine or test expectation.
3. F1: isolate encoder vs decoder, ORT verbose log to identify the fused node; compare against tiny/base staged graphs; check `BuildTensorRtRtxOptions` profiles. Possible outcome: large-v3 stays on CUDA/fallback and flip covers only validated sizes (requires narrowing the all-or-nothing flip).
4. Only then: `Flip-WhisperOnnxTrtRtx.ps1` (or narrowed variant) → `dotnet build` + full `Trackdub.Inference.Tests` → commit.

## Caveats for the debugger
- `build/whisper-onnx-trtrtx-validation.json` reflects the LAST Olive run only (currently large-v3). Per-size evidence is in `build/whisper-<size>-onnx-trtrtx-validated/` dirs + shell history.
- Olive runs were `-SkipLatency` (Librispeech unavailable); latency path unexercised.
- All Olive validations + C# tests above ran on THIS machine (RTX 5070); results may not transfer.
- venv: `%LOCALAPPDATA%\Trackdub\tools\olive-env-tensorrtrtx` (onnxruntime 1.30.0 per `TrtRtxOliveCommon.ps1`); EP bundle: `%LOCALAPPDATA%\Trackdub\Providers\trt-rtx\0.3.0\cu12\win-x64`.
