# Inference pipeline audit: cold starts and model code

## Verified snapshot

Audited `MesilimLLC/Trackdub` on 2026-10-24/25. Every finding was confirmed against live
source, not inferred from names or comments. Progress below is verified against `main` at
`92dc8f00` (merge of PR #533).

Evidence method: two deep research passes over the inference surface (session pool, session
factory, TensorRT cache, pipeline orchestration, mel math) plus a baseline measured on real
media with real models on a Windows host (RTX 5070, 32 GB RAM).

## Why the ranking changed during the work

The audit was written before any measurement existed. A baseline run then changed two
conclusions, and this document records the corrected view rather than the original guess:

- A 6.3 s clip through the full pipeline (VAD, diarization, ASR, translation, separation, TTS,
  export) took 298 s wall. Sum of all stage work was **21 s**. About **86% of the run was gaps,
  not inference.**
- The largest gaps were 138 s before the first stage and 92 s before diarization. Phase
  instrumentation attributed both to TensorRT engine work inside plan-time smoke, not to the
  model-code inefficiencies in Part 2.
- Therefore the model-code items are real defects but were **not** what slowed a short pipeline.
  They matter on long media. The cold-start items dominate on short media.

## Part 1: cold-start bottlenecks

### C1. HIGH — global `bundleAcquireLock` serializes all multi-graph session creation

`InferenceSessionPool.cs:1175`. The whole Phase 1 create loop (one factory per graph, each a
multi-second session create or multi-minute TensorRT engine build) plus the re-warm retry loop
runs under one process-wide lock. `PipelineReadinessService` evaluates three stages
concurrently, and most heavy stages are bundles (Whisper, MADLAD, Qwen3-ASR, LatentSync), so a
cold multi-stage pipeline pays the **sum** of bundle build times instead of the max.

The lock exists to stop opposing bundles deadlock-pinning each other. Any fix must preserve
that: acquire admission reservations in the existing `SessionPoolKey.StableComparer` order, or
parallelize per-graph creation and serialize only pin/publish in key order.

### C2. HIGH — TRT engine build inline; capture silently disabled on low disk

Measured in-repo: ~48 s build vs 3.6 s artifact load for MADLAD-400 3B fp16 encoder. Two
separate problems:

1. Nothing warmed after a model install, so the first run paid the full build on the request
   path.
2. Below 2x source bytes of free disk, `ShouldCapture` returned false with only a trace-level
   warning, so every later launch rebuilt every engine with no surfaced diagnostic.

### C3. MED-HIGH — triple full-file reads of multi-GB models, hashed serially

Each pooled key build streams the whole `.onnx` for a SHA-256 (three reads per graph once the
TRT unsupported-op scan and ORT parse are counted). Multi-graph keys also hash serially:
encoder before decoder, Qwen3-ASR's three graphs in sequence, LatentSync's four. Digests are
cached by (path, length, mtime), so steady state is fine; the first cold pass is not.

### C4. MED — first-run smoke test runs a full session create plus inference inside planning

`RuntimePlanFactory` runs `OnnxExecutionProviderSmokeTester.SmokeTestAsync` on the first plan
per (model, provider, environment) when no persisted verdict exists. This is intended
"never fake readiness" behavior and verdicts persist, but first-run cold start pays one full
session-create plus inference per stage, per provider candidate, serially through the fallback
loop. **After C2, this is the dominant remaining pre-stage cost**: instrumentation showed
`ep-smoke` at roughly 1.9 s per smoke session build.

### C5. MED — inference worker lazy-spawned with no warm message

The worker spawns and handshakes on the first CUDA TTS call (60 s handshake timeout), and
Kokoro is constructed lazily inside the worker on first use. The protocol carries only
`hello`, `tts.synthesize`, `cancel`, `shutdown`. First CUDA TTS inference pays process spawn
plus .NET startup plus handshake plus engine construction plus session create plus first Run.

### C6. MED — `GenAiModelPool` capacity of 1 disables lookahead warmup for GenAI stages

Text refinement (Qwen), GenAI translation (Phi), and GenAI ASR share one pool entry, so every
GenAI stage transition evicts and fully reloads the native model. `DubbingPipelineEngine`
explicitly disables next-stage warmup to work around it.

### C7. MED — sync-over-async on first-touch paths

- `CompositionRoot.cs:711` — scoped `IVoiceCatalog` registration blocks on
  `.GetAwaiter().GetResult()` during voice catalog enumeration, re-executed per scope.
- `EpContextLoadPathResolver.cs:58` — static `Lazy` hardware probe blocks on registry/GPU
  probing during first TRT session creation.
- `LocalModelCacheRecordLookup.cs:16` — reloads the whole record store synchronously per
  `Find`, no caching.
- `HeadlessDubbingSessionFactory.cs:86` — license init blocks on first session creation.

### C8. MED — serial hash verification of multi-GB models on install/verify

`RuntimeModelBootstrapService.VerifyRequiredFilesAsync` (`:858`) hashes each required file
serially, as does `ModelDownloadOrchestrator`. First install of a dub pipeline is a serial
multi-GB read pass. Steady-state runs do not re-hash, so this is install-path only.

### C9. MED — no production cold-start timing evidence

`BenchmarkPhaseCapture` had the right phases but only activated under a benchmark host, so
there was no pipeline-start to first-inference measurement anywhere. This blocked ranking
everything above by real elapsed time.

### C10. LOW

- `PinPooledSingleAsync` retries residency pinning with fixed sleeps (50/100/200/400 ms)
  instead of waiting on the pool state signal.
- `DeviceFallbackSessionCreator` allows up to 8 full session-create attempts; under VRAM
  exhaustion cold start degrades to repeated multi-second creates.
- `IntraOpNumThreads` is never set, so N concurrent CPU session creates each default to
  all-core thread pools and oversubscribe the CPU during cold start.
- The 23.5 s first-project-open outlier in `docs/reference/profiling-report.md` was never
  diagnosed and looks like C2/C3 territory.

## Part 2: model code

### M-H1. HIGH — Qwen3-ASR reloaded a ~300 MB embedding file per request

`TranscribeAsync` called `Qwen3AsrEmbedTokens.Load(...)` and `Qwen3AsrTokenizer.LoadAsync(...)`
on every request: a full file read plus a 151,936 x 1024 `float[,]` (LOH-scale) per
transcription, with no cache, while `SidecarCache<T>` already existed for exactly this pattern.

### M-H2. HIGH — Qwen3-TTS copied the entire KV cache every decode step

Each of up to 2048 steps allocated fresh copies of all layers' KV, so allocation and copy cost
grew quadratically with output length. Same pattern per code-predictor group per step (15
groups x T steps).

### M-H3. HIGH — Chatterbox had the same quadratic churn plus O(T^2) token/mask arrays

`generatedTokens.Concat([nextToken]).ToArray()` and `attentionMask.Concat([1L]).ToArray()` per
step, `generatedTokens.ToHashSet()` rebuilt every step inside `SelectNextToken`, and
`PastTensor.FromOutput` doing `ToArray()` per past-KV per step.

### M-H4. HIGH (blocked upstream) — OpusMt decoded without the KV cache

`use_cache_branch` was always `false` with empty past tensors, so `GreedyDecodeAsync` re-ran
the full generated prefix every step. `Seq2SeqGreedyDecoder` already implemented the cached
decode that MADLAD uses.

**Blocked, not done.** The bundled `decoder_model_merged.onnx` does expose the cache interface
(`use_cache_branch` plus 24 `past_key_values.*` inputs and 24 `present.*` outputs), but its
cache branch throws `Starts must be a 1-D array` inside `optimum::if` on step 1 with
`use_cache_branch=true`. Reproduced on all four installed pairs (`en-es`, `en-de`, `en-fr`,
`es-en`), and step 0 succeeds with identical shapes, so the defect is in the export, not the
caller. This needs an upstream re-export; the audit's "pure win" framing was wrong for this
artifact.

### M-H5. HIGH (conditional) — linear resampling with no anti-aliasing filter

`Audio/AudioResampler.cs` resamples by direct linear interpolation. Downsampling 44.1/48 kHz
to 16/24 kHz aliases content above the new Nyquist into the passband. Whether this is live
depends on whether pipeline normalization always delivers target-rate audio; the upstream
stage was never verified.

### Medium

- **M1** Whisper computes a full 30 s mel (3000 frames) per chunk regardless of actual length,
  so a 3 s VAD region pays ~10x the STFT cost.
- **M2** Whisper tokenizer (~52k entries) re-parsed from JSON per transcription; mel-bin count
  re-read per call.
- **M3** Nemotron managed decoder does per-step metadata lookups and shape resolution; the mel
  filterbank is built twice per region.
- **M4** Qwen3-TTS round-trips audio through a temp WAV on disk instead of returning samples.
- **M5** Qwen3-TTS disposes and reloads the prefill session every synthesis, and its runtime
  sessions bypass `OnnxExecutionSessionFactory` pooling entirely.
- **M6** Qwen3-TTS sampling does a full-vocabulary LINQ sort per generated token.
- **M7** SCRFD face detector creates a new `InferenceSession` per detection call.
- **M8** GenAI engines construct a `Tokenizer` per request while the `Model` is pooled.
- **M9** Qwen3-TTS `MelSpectrogram.ReadWav` reads samples one `ReadInt16()` at a time.
- **M10** No golden/parity numeric tests for any mel implementation; silence-only model runs
  cannot detect mel math errors.

### Low (selected)

Qwen3-ASR silently drops too-short chunks without a skip reason; Whisper `SelectNextToken`
uses the slow 3-D indexer where a row-span read exists; `LastEncodedAbsSum` LINQs the whole
tensor per chunk; Parakeet decoder per-step metadata lookups; Qwen3-ASR `Lookup` allocates a
`float[HiddenSize]` per generated token; Qwen3-TTS `EmbeddingStore` uses a non-SIMD
bounds-checked 2-D matmul per token.

## What was correct and should be preserved

- **Mel/STFT math is correct.** `QwenMelSpectrogramMath` uses the HTK mel scale with
  librosa-style max-8 clamp and `(x+4)/4` normalization; MathNet
  `FourierOptions.Matlab` matches `torch.stft`'s unnormalized FFT. Whisper, Qwen, and Nemotron
  mel banks, frame counts, and reflect padding all match their references.
- **Whisper TRT-RTX bound path**: pinned `DeviceBoundTensor`s, io-binding, single CUDA-graph
  replay per step with documented causal masking.
- **Nemotron device-bound encoder/decoder**: positional binding, separate in/out cache buffers
  avoiding ORT aliasing, chunk prefetch, correct RNNT blank semantics.
- **`Seq2SeqGreedyDecoder`**: proper KV-cache decode with TRT static-shape bucketing.
- **`DeepFilterNetSignalProcessor`**: meticulous libDF parity with dedicated tests.
- **EP-context artifact caching is well keyed** (GPU arch, driver, fingerprint, ORT version,
  compile-options identity, device compatibility), and the warm service classifies compiled vs
  reused by actual cache growth rather than trusting session creation.
- **Pool hygiene**: single-flight creation with failure-wave propagation, LRU plus residency
  pins, per-bucket memory admission, content-hash caching so repeat pool-key builds never
  re-read multi-GB models.
- **Honest evidence culture**: bootstrap details, verified EcoQoS read-back, `EngineCacheProbe`
  refusing unattributed cache hits.

## Progress

Status verified against `main` at `92dc8f00`.

### Done and merged

| Item | What shipped | Evidence |
| --- | --- | --- |
| **C9** | `ColdStartCapture` plus phase attribution; evidence on `DubbingRunResult` and the run manifest; summary log line | Produced the numbers that reordered this audit |
| **C2a** | `TryGetSkipReason`, non-memoized space skips, once-per-model warning, `doctor` engine-capture headroom check | Engine artifact written; session create 25.4 s to 5.8 s |
| **C2b** | Auto-warm queue triggered from the install path in `ModelDownloadOrchestrator` | Real `DownloadAsync` test reaches the queue |
| **M-H1** | `Qwen3AsrSidecarCaches` for embed table and tokenizer | 5 new cache-contract tests |
| **M2** | `WhisperSidecarCaches` for tokenizer and mel bins, invalidated on install | Mutation-checked: removing a flush fails the test |
| **M-H2** | Pre-allocated KV buffers, code-predictor sized for its 16 positions | Real synthesis: 20.44 s take, peak 0.55, RMS 0.034 |
| **M-H3** | `List`/`HashSet` instead of per-step `Concat`/`ToHashSet` | Same real synthesis run |

### Measured impact

- TensorRT session create: **25.4 s to 5.8 s** (4.4x) once the engine artifact exists.
- Diarization stage: **4.2 s to 1.6 s**.
- Before/after the whole body of work: the 298 s baseline was dominated by gaps that C2
  removed; C2 is why the fix list below no longer leads with engine builds.

### Blocked

- **M-H4** — needs an upstream ONNX re-export of the Opus merged decoder. No code change in
  this repo can fix it.

### Not started

Verified still present in source at `92dc8f00`:

- **C1** `bundleAcquireLock.WaitAsync` at `InferenceSessionPool.cs:1175`.
- **C3** `BuildDualPooledKeysAsync` still sequential (3 call sites).
- **C4** smoke still runs a full session create inside planning. **Highest remaining value.**
- **C5** worker protocol still lacks a warm message.
- **C6** `GenAiModelPool` `maxEntries = 1`.
- **C7** sync-over-async at `CompositionRoot.cs:711` and `EpContextLoadPathResolver.cs:58`.
- **C8** serial hash loop at `RuntimeModelBootstrapService.cs:858`.
- **C10**, **M-H5**, **M1**, **M3** through **M10**.

Tally: **7 done, 1 blocked upstream, 16 not started** (24 findings).

## Recommended next steps

1. **C4** — persist and reuse smoke verdicts so warm sessions carry across launches, or move
   smoke off the planning critical path onto the existing background warmup coordinator. This
   is now the dominant pre-stage cost.
2. **C1 and C3** — parallel per-graph key hashing and reconsider the bundle lock, keeping the
   pinned pool tests green.
3. **C7** — the DI sync-over-async spots are small, mechanical, and low-risk.
4. **M-H5 and M10** — verify input sample rates before changing resampling, and add golden mel
   fixtures before any mel refactor so the change is provably neutral.

## Reproducing the measurement

The baseline that reordered this audit used the CLI against a short real clip with real models:

```bash
trackdub run pipeline --media <clip.mp4> --target-language es \
  --model asr:whisper-small --model translation:opus-en-es --model tts:qwen3-tts-0.6b \
  --output <dir> --progress text
```

Read `coldStartTiming` from the produced `run-manifest.json`: it carries per-phase totals
(`ep-smoke`, `onnx-session-create`, `runtime-plan`, `model-provision`, and others) alongside the
pipeline-start to first-inference elapsed time. Compare `firstInferenceCompletionElapsedMs`
before and after a change.
