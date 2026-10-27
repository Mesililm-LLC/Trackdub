# GenAI model pool capacity

`GenAiModelPool` (text refinement, GenAI translation, GenAI ASR) resolves its resident
entry capacity from hardware via `GenAiModelPoolCapacity.Resolve`, in the same
clamped-derivation style as the session-pool budgets: a positive
`TRACKDUB_GENAI_POOL_MAX_ENTRIES` always wins; otherwise capacity is
`clamp(max(acceleratorBudgetMb, hostBudgetMb) / LargestMeasuredEstimateMb, 1, 3)`,
where the budgets are the admission pool's scaled budgets (75% of VRAM at or below
16 GiB on the accelerator side, host RAM budget capped at 16,384 MiB). Per-bucket
memory admission in `InferenceSessionPool.ReserveExternalAsync` remains the real
memory guard; the entry count only decides when idle models are evicted.

## Measurement (2026-10-26, RTX 5070, 12,227 MiB VRAM, 65,435 MiB RAM)

File sizes are the `Content-Length` of the downloads pinned in
`src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`
(`download_file_sources`, or the source repo at the pinned revision for Whisper).
The resident estimate is the pool's own `GenAiModelKey.EstimateMemoryMb` formula
(2x file bytes + 256 MiB, floored at 1,024 MiB):

| Model (stage, variant) | On disk | Estimated resident |
| --- | ---: | ---: |
| Qwen2.5-1.5B-Instruct (text refinement, default) | 5,916 MiB (model.onnx.data 5,905 MiB) | 12,086 MiB |
| Phi-4-mini-instruct (GenAI translation, gpu-int4) | 3,276 MiB | 6,808 MiB |
| Whisper small (GenAI ASR) | 1,078 MiB | 2,412 MiB |
| Whisper tiny (GenAI ASR) | 224 MiB | 1,024 MiB (floor) |

## Gate result for 12 GB

The two largest estimates sum to 18,894 MiB, above both the 12 GB card's scaled
accelerator budget (9,170 MiB) and the 16,384 MiB host-budget cap. Only one GenAI
model fits with headroom, so the scaled default is **1** on this class of hardware —
the effective capacity is deliberately not forced to 2. Capacity 2 requires an
admission budget of at least 24,172 MiB (e.g. a large-VRAM card), and 3 is the cap
(the number of GenAI stages in a pipeline).

When the resolved capacity is at least 2, `DubbingPipelineEngine` re-enables
concurrent next-stage warmup (the coordinator's `AllowsConcurrentWarmup`): the next
GenAI model loads while the current stage still holds its lease. At capacity 1 the
previous behaviour is kept — warmup starts only after the current stage releases
its model, so it cannot evict the active model.

GenAI warmup itself is non-evicting at any capacity
(`GenAiModelPool.TryGetWarmupLeaseAsync`): it leases the target only when that
model is already resident or a free entry slot exists whose admission fits without
eviction. Concurrent warmup starts before the current stage re-leases its model,
so that model is resident-but-idle with the pool's oldest release tick — exactly
the entry an evicting acquire would discard. When no non-evicting lease is
possible the warmup is recorded as skipped (`genAiModelWarmupSkipped` /
`stageWarmupSkipped`, `Attempted: false`) and the stage loads inline; the real
stage acquire keeps the regular evicting behaviour.
