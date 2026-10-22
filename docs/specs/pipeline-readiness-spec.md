# Pipeline transient-failure coverage spec

> Current AWS POC contract: [SnapSoft application acceptance](../operations/snapsoft-poc-acceptance.md). Historical API/Swagger/compose/telemetry examples below are not evidence of current wiring. The new CPU API uses DynamoDB/S3, contains no inference, and has no billing/OTel exporter. GPU compose runs the one-shot CLI; it does not expose HTTP.

**Status:** Draft (2026-07-22). Spec only; does **not** close a BACKLOG row.
**Lane:** Fault tolerance, pipeline-wide orchestrator, cross-cutting reader surface.
**Coverage:** Cancellation + directory/file lock + transient download + transient OOM.
**Cross-platform parity:** `net10.0` + `net10.0-windows10.0.19041.0` + Linux/macOS shared tail; Avalonia UI tier stays on Windows TFM.
**Audience:** engineers + headless SDK/CLI/Worker + Avalonia VM + Trackdub doctor handler.

## 1. Problem statement

Trackdub's pipeline already labels failures ("Blocked", "Failed", "Skipped — valid artifacts from prior run", "Failed: speech-enhancement: …") and emits a sequence of `PipelineProgressEvent` records plus a SQLite `StageRunRecord` per stage. But transient failures — ones that should not turn the run into a hard fail and that the user should understand are recoverable — are not classified, surfaced, or counted anywhere.

Evidence:
- `src/Trackdub.Application/Dubbing/DubbingPipelineEngine.cs:1314-1316` — `IsBenignSkipReasonCode` only knows about a small set of benign codes (e.g. `EXISTING_ARTIFACTS_VALID`); everything else falls into the "failed" bucket.
- `src/Trackdub.Domain/StageRuns/StageSkipReasonCodes.cs:8-29` — single constant `ExistingArtifactsValid` plus the benign set; no enum.
- `src/Trackdub.Application/Transcripts/StageRunHygiene.cs:13-58` — only reconciles stale `Running` rows after crash, not transient failures during a live run.
- `src/Trackdub.Application/Transcripts/Pipeline/TranscriptPipelineBuilder.cs:82-110` — the resume path silently skips without an upstream "transient" marker.
- `src/Trackdub.Application/Transcripts/Pipeline/TranscriptPipelineResumeHydrator.cs` — re-uses prior artifacts with `Status == Completed`; cannot tell apart a transient failure that snuck in before a successful artifact.
- `src/Trackdub.Inference/Pool/InferenceRetryPolicy.cs:70` — already pulls `[ErrorCode:XXX]` from `OnnxRuntimeException` but only paper-trails; never publishes to a stream.
- `src/Trackdub.Infrastructure/Licensing/ParallelRangeDownloader.cs:70-87` — stopwatch + bytes/sec; `CancellationToken` flow honored but no classification.
- `src/Trackdub.App.Avalonia/Services/OperationRunner.cs:30-69` — `OperationRunnerLane.Load` vs `Pipeline` lane semantics; cancellation propagation is correct but untyped.
- Avalonia `PipelineStageRowViewModel.cs:265-320` — only knows Completed/PartiallyCompleted/Skipped/Running/Failed; no "transient retry" state.
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs` — current section list does not include a transient-fault summary; no end-to-end postmortem correlation.

Net effect: a transient cancel or download-retry storm looks identical to a real failure in the SQLite rows, the UI row icon, the run-manifest JSON, and the diagnostics bundle. There is no honest signal for "the user pressed Cancel halfway through ASR" vs "ASR failed". The acceptance posture "never fake readiness / no silent failures / honest per-stage states" (per `MILESTONE.md` §Current non-negotiables) cannot be satisfied for transient cases today.

## 2. Goals

1. Define one typed `TransientFailureKind` enum that classifies the four modes in scope plus a small set of near-cousins. Subscribers can pattern-match on the kind, not string-match on ad-hoc reason codes.
2. Introduce one `PipelineTransientFault` record (per-fault signal) + one `PipelineTransientFaultBus` (in-process pub/sub bounded to the last 50 faults). Three writers, four readers minimum.
3. Surface the bus three ways:
   - Through `DubbingRunStatus`/`StageRunRecord` so the SQLite state is honest about transient retries.
   - Through `RunManifest.transient` key so SDK/CLI/Worker/headless batch consumers can audit.
   - Through a new `DiagnosticsBundle.transient` section so post-mortem docs include them.
4. Add `WorkerMetrics.transient_total` counter.
5. Wire the existing `CancellationToken` flow at `StageRunHelper.RunStageAsync` so it writes a `StageRunRecord` with `Status = Canceled` *before* re-throwing — no half-state where SQLite still says Running.
6. Tests cover each of the four transient classes (cancel, lock, download, OOM) × each of the four read surfaces (StageRunRecord, RunManifest, DiagnosticsBundle, Avalonia row).

## 3. Non-goals

- Permanent-failure contract extension stays out of scope; existing `StageSkipReasonCodes.IsBenignSkipReasonCode` continues to govern the benign-skip path.
- This spec does **not** touch `AvaloniaMainWindowViewModel` UI chrome beyond a single `LastTransientText` string on `PipelineRunViewModel`. Visual refresh is in a follow-up spec.
- No new external telemetry sinks (no OpenTelemetry, no Sentry). The bus is in-process only.
- No SDK breaking change beyond a new additive `IAsyncEnumerable<PipelineTransientFault>` accessor on `IDubbingPipelineEngine` and a new `RunManifest.transient` key. Old fields untouched.
- OOM classification remains best-effort heuristic — a precise OOM-by-CPU-budget ADR is its own future backlog candidate.
- No backport to closed BACKLOG rows. P0-1 evidence from 2026-06-10 stays valid; this is a parallel track.

## 4. Proposed design

### 4.1 `TransientFailureKind` enum
- Lives in `src/Trackdub.Domain/Pipeline/TransientFailureKind.cs`.
- Members:
  - `UserCancellation` — caller's `CancellationToken` fired; never retry, write `Canceled` row.
  - `DirectoryLock` — a process or app holds the directory/file exclusively; retry with backoff.
  - `SqliteBusy` — SQLite `SQLITE_BUSY`; retry, same backoff.
  - `FfmpegProcessExit` — ffprobe/ffmpeg crashed mid-op (often transient driver state); retry.
  - `ModelDownloadTransient` — HF/mirror returned 5xx, network glitch; retry.
  - `StarterPackTransient` — 7zr crash or tar exit non-zero + archive integrity OK; retry.
  - `MemoryExhausted` — inference host ORT/OnnxRuntime reported memory pressure; backoff + downscale quant.
  - `DeviceTimeoutTransient` — DirectML/TensorRT-RTX/WinML catalog stalled on a hot plug; retry.
  - `Unknown` — fallback when no classifier matches; logged, classified as retriable once.
- Static helper `bool IsTransient(Exception ex)` returns true iff the exception's type or message maps to any of the above. Defaults false (fail fast).

### 4.2 `PipelineTransientFault` record
- Lives in `src/Trackdub.Contracts/Pipeline/PipelineTransientFault.cs`.
- Fields: `Guid ProjectId`, `string StageName`, `TransientFailureKind Kind`, `string Detail`, `DateTimeOffset HappenedAt`, `int AttemptNumber`, `IReadOnlyDictionary<string,string>? Context` (free-form: path, exception type, exit code, etc.).

### 4.3 `PipelineTransientFaultBus`
- Lives in `src/Trackdub.Application/Transcripts/Pipeline/PipelineTransientFaultBus.cs`.
- Singleton, scoped per project run via `projectSession`. Bounded ring buffer (last 50). Exposes `IObservable<PipelineTransientFault> Stream` for live subscribers.
- Method `Publish(PipelineTransientFault fault)` is idempotent under cancellation (publishes nothing if the parent `CancellationToken` is already cancelled unless the fault's Kind is `UserCancellation` itself — exception so the user-action event is never silenced).
- Snapshot accessor `IReadOnlyList<PipelineTransientFault> Snapshot()` for the postmortem write.
- **Shipped scope (as of the §4.4/§4.5 wiring PR):** the bus is registered as a Composition-level singleton (`HeadlessCompositionRoot.AddHeadlessTrackdub` → `services.AddSingleton<PipelineTransientFaultBus>()`), not scoped per project run. `DubbingPipelineEngine` and `DiagnosticsBundleExporter` both resolve the same instance for the lifetime of the host, so faults from different runs share one ring buffer. Per-run scoping remains a candidate follow-up (see §9.1).

### 4.4 Wiring at `StageRunHelper.RunStageAsync`
- This is the single chokepoint across `Vad`, `Asr`, `Diarization`, `SpeechEnhancement`, `StemSeparation`, `SpeakerAssignment`, `Translation`, `TTS`, `LipSync`, `LipSynthesis`, `OverlapRescue`, `Export`. One wiring edit covers all.
- Pattern: `try { ...existing body... }` extended with:
  - `catch (OperationCanceledException)` → emit `UserCancellation`, write `StageRunRecord.Status = Canceled` via `StageRunHelper.RecordCancelAsync`, re-throw.
  - `catch (Exception ex) when (TransientFailureKind.IsTransient(ex))` → emit `((kind, attempt))`, leave `Status = Running` for one more retry attempt, re-throw.
- A small retry helper `RunStageWithTransientRetryAsync(...)` does up to 3 attempts of the inner body, doubling backoff (50ms, 100ms, 200ms) per same-kind fault within the same stage.
- **Shipped behavior differs from the bullet above:** `StageRunHelper.RunStageAsync`'s transient catch has no retry loop of its own, so it now calls `FailAsync` (terminal `Failed` row) before re-throwing instead of leaving `Status = Running`. Leaving the row `Running` only applies inside `RunStageWithTransientRetryAsync`, which owns its own `StageRunRecord` lifecycle across attempts. Callers that want retry semantics must go through `RunStageWithTransientRetryAsync`; direct `RunStageAsync` callers always see a terminal row on a transient failure.
- **Shipped follow-up — retry-budget extraction:** the 3-attempt / doubling-backoff parameters above are no longer hardcoded inline. They live in the `StageRetryBudget` domain record (`src/Trackdub.Domain/Pipeline/StageRetryBudget.cs`): `MaxAttempts` (1–10), `BaseBackoffMs` (>= 0), `MaxBackoffMs` (default 51,200ms), with `BackoffFor(attempt)` computing the doubling delay. `RunStageWithTransientRetryAsync` takes an optional `retryBudget` parameter and falls back to `StageRetryBudget.Default` (`MaxAttempts: 3, BaseBackoffMs: 50`), which reproduces the 50ms/100ms/200ms sequence above unchanged. Callers may inject a tighter budget (e.g. `MaxAttempts: 1`) without touching the retry-loop body. See §11.9.

### 4.5 Reader surfaces

- `DiagnosticsBundleExporter`: append a `transient` section with `Total`, `CountsByKind`, `MostRecent[]` (max 20). Schema in `src/Trackdub.Infrastructure/Diagnostics/TransientFaultSummary.cs`. Persisted in the run-manifest JSON under the same key.
- `IDubbingPipelineEngine`: gain `IAsyncEnumerable<PipelineTransientFault> TransientFaults { get; }`. Stream consumers in `Trackdub.Cli` (DoctorHandler `--explain-transient`), `Trackdub.Worker` (counter emissions), `Trackdub.Sdk` (tests).
  - **Shipped shape:** the surface landed as a separate `ITransientFaultReporting.TransientFaultsAsync(CancellationToken cancellationToken = default)` method (not a `TransientFaults` property on `IDubbingPipelineEngine`), implemented by both `DubbingPipelineEngine` and the SDK's `TrackdubDubbingEngine` (which forwards to the inner engine). It bridges the bus's `IObservable<PipelineTransientFault>` into a bounded `Channel`-backed `IAsyncEnumerable`, so subscribers see live faults for the duration of enumeration rather than a one-time snapshot.
- `AvaloniaMainWindowViewModel.PipelineUi`: bind `PipelineRunViewModel.LastTransientText` (string) updated via `ApplyTransientFault` event handler. UI row icon stays the existing one; transient overlay badge shows "retrying (N)" while `applied Faults.Count > 0`. After all settle, transient counters become invisible unless doctor / diagnostics open them.
- `Trackdub.Worker/WorkerMetrics`: emit `transient_total` counter per stage × per kind.

### 4.6 Cancellation token honesty

- `OperationRunner.TryRunAsync` already threads `CancellationToken`. The orchestrator must:
  - Honor the token at every `await`.
  - On cancel, write the canceled `StageRunRecord` row *before* re-throwing so SQLite doesn't lie about an `Running` row that was actually canceled halfway.
- This is a one-line addition to the catch block; the existing `OnFrameworkInitializationCompleted` crash scaffolding in `App.axaml.cs` is upstream of this concern and stays untouched.

## 5. Acceptance criteria

- `dotnet build Trackdub.sln -m:1 -p:Platform=x64 --no-restore` clean (Trackdub.Avalonia multi-target + Windows-only test TFM allowed).
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Worker.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Cli.Tests --no-restore -m:1 --filter "FullyQualifiedName~Transient"` 100% green.
- `dotnet format --verify-no-changes` clean for every touched project.
- When the headless smoke runs (`tests/Trackdub.Sdk.Tests/HeadlessPipelineSmoke`) with a synthetic transient in the stub providers, `run-manifest.json` gains a top-level `transient: { countsByKind: {...}, mostRecent: [...] }` shape matching §4.5 schema.
- Doctor handler enumerates the new `TransientFailureKind` codes via an `--explain-transient <kind>` flag in `Trackdub.Cli/Handlers/DoctorHandler.cs`.
- Avalonia `PipelineRunViewModel` tests pass when `ApplyTransientFault` receives a sequence of three `DirectoryLock` events then a success — `LastTransientText` reflects the latest.

## 6. Tests plan

- **Unit (Domain)**: `TransientFailureKind.IsTransient(ex)` returns expected bool for: `OperationCanceledException`, `IOException` with `[ErrorCode: 32]` (locked), `SqliteException` with `SQLITE_BUSY`, `OnnxRuntimeException` with `[ErrorCode: 4]`, `HttpRequestException` with 5xx, `OutOfMemoryException`. Plus negative cases (`ArgumentException`, `NullReferenceException`) returning false.
- **Unit (Application)**: `PipelineTransientFaultBus` publish + 50-cap overflow correctness. Snapshot order = arrival order.
- **Integration (Application)**: `StageRunHelper.RunStageWithTransientRetryAsync` with a fake that throws on the first N attempts and succeeds on the N+1. Verify bus receives N events of same kind and final StageRunRecord has `Status = Completed`.
- **Integration (SDK/Worker)**: a stub DubbingPipelineEngine that emits three `ModelDownloadTransient` events; assert `IAsyncEnumerable<PipelineTransientFault> TransientFaults` yielded all three; `WorkerMetrics.transient_total` = 3.
- **Headless**: `HeadlessPipelineSmoke` with a fake stage that throws one `DirectoryLock`; assert `run-manifest.json.transient.countsByKind.DirectoryLock = 1` and the snapshot diagnostic export contains a `transient` section.
- **Avalonia**: PipelineRunViewModel tests asserting `ApplyTransientFault` updates `LastTransientText` correctly across cancel / lock / download / success sequences. UI Tests may stay on Windows TFM; the binding logic is TFM-agnostic.
- **Regression**: existing `PipelineDegradationWriterTests`, `AsrDeviceDegradationTests`, `HeadlessPipelineSmokeTests` must still pass unchanged.

## 7. Cross-platform notes

- Avalonia UI tier stays `net10.0-windows10.0.19041.0` for Windows-only tests. The VM, bus, and types are TFM-agnostic.
- Linux/macOS: file-lock semantics differ. `DirectoryLock` classification uses `IOException` with `HResult == 0x80070020` (ERROR_SHARING_VIOLATION) on Windows and `[Errno 11] EAGAIN` / `[Errno 35] EDEADLK` on POSIX; classifier is platform-gated via `#if WINDOWS / #elif LINUX / #elif MACOS`.
- Storage paths that differ by platform: `IOException.HResult` on POSIX is `Marshal.GetLastWin32Error` style not directly available — the classifier uses `ex.Message` regex on POSIX as fallback.

## 8. Risk + alternatives (recorded for future ADR candidates)

- (a) Bounded bus cap (50). A 1000-fault/minute retry storm could overwrite the first 950. Alternative: stream-only; no snapshot file. Trade-off favors bounded + snapshot for repr() stability under Android-like storms.
- (b) OOM classification. Heuristic by exception type + log message. False positives are possible (legitimate `NullReferenceException` with "OOM" in message). Future ADR can tighten.
- (c) Backwards-compat of `RunManifest` JSON. Adding a top-level `transient` key is additive and does not break existing schema. The spec treats everything else as frozen.
- (d) Whether `TransientFailureKind` belongs on Domain or Contracts. Domain is currently chosen (the enum is purely a data shape, not an SDK API). SDK imports Domain for stream element type. If a future ADR rejects that, an option is `Contracts.Pipeline.TransientFailureKind` with a Domain re-export.

## 9. Open Questions — ADR candidates (fleshed)

Each question below is structured for promotion to a discrete ADR. Status: **Candidate** (not yet numbered; this spec does not own the ADR sequence). Promotion criteria are explicit per question.

### 9.1 Candidate ADR: per-run aggregation strategy

- **Slug:** `pipeline-transient-aggregation`

**Problem.** §4.3 ships a 50-event ring buffer + per-stage counters. The companion question is whether the snapshot the bundle exposes should also aggregate across stages into a single per-run summary (e.g. `transient.countsByKind` summed across all stages of one project) — and where that aggregation lives.

**Options considered.**

- (a) Snapshot per-run only via the existing `DiagnosticsBundle.transient` section. No in-memory aggregate; per-stage counts only.
- (b) In-process `PipelineTransientFaultBus.SnapshotPerRun(Guid projectId)` returning a per-stage × per-kind roll-up. Filter at the bus boundary, not at the consumer.
- (c) SQLite runtime rollup table `PipelineTransientFaultCounts` updated synchronously on every `Publish`. Persisted, queryable, but adds a write per fault.
- (d) Stream-only; aggregate callers re-filter from `IObservable` with their own windowing.

**Trade-offs.** (a) is light but loses cross-stage correlation. (b) keeps SQLite schema frozen, gives fast aggregate, degrades cleanly when the bus is dropped. (c) yields persistence for free but adds latency to every fault and a new migration; per-`Publish` SQLite roundtrip is wasteful. (d) puts the work on every consumer and forces everyone to re-rollup.

**Recommendation.** (b). It honours the spec’s “schema frozen, additive only” gate, caps in-proc memory at the existing 50-event ring, and the per-stage snapshot in the bundle stays accurate without a write storm. Persisted rollups can be derived later if needed.

**Promotion criteria.** Promote to a discrete ADR when one or more of the following is true:
- A new consumer (e.g. the Web dashboard or a future hosted variant) asks for cross-stage fault aggregation across a run.
- A user-facing report (e.g. “this run had 14 transient faults and your project retried 9 times”) is added to the UI.
- A diagnosis surfaces where knowing the fan-out across stages, not the per-stage count, is the differentiator.

### 9.2 Candidate ADR: OOM classification scope

- **Slug:** `pipeline-oom-classification`

**Problem.** `TransientFailureKind.MemoryExhausted` is one of the eight codes in §4.1, but the classifier is heuristic (exception type + log message). The question is whether OOM classification lives inside the transient-failure spec or becomes its own ADR that produces a separate signal.

**Options considered.**

- (a) Bounded to this spec. OOM is one of the eight transient kinds; classifier heuristic ships along with the rest.
- (b) Separate ADR for an OOM-only signal (`MemoryBudgetExceeded`, sensor-driven, cgroup/ORT-arena aware).
- (c) Split: OOM stays in the transient-failure spec; the broader memory-budget controller + classifier precision lands in its own ADR later.

**Trade-offs.** (a) keeps the spec scoped but lets a heuristic do work where a sensor would do better. (b) defers a real problem but forces a tiny OOM classifier into 9.2 + the existing sketch. (c) keeps the time to ship short and reserves the proper ADR for when signal improves.

**Recommendation.** (c). The existing `src/Trackdub.Application/Transcripts/DeviceFailureDegradationFactory.cs:36-43` already carries an OOM-oriented degradation record; the transient surface reuses the same boundary today. Promote when memory sensors become available.

**Promotion criteria.** Promote when any of the following is true:
- A concrete memory sensor (cgroup limits on Linux, ORT memory-arena caps, Avalonia render-frame budget tracking) is added and reports per-stage usage.
- OOM-classified faults start surfacing in user-visible UI (e.g. a banner on the pipeline panel) and need a richer payload than `TransientFailureKind.MemoryExhausted` can carry.
- A second OOM source (e.g. GPU memory on DirectML/CUDA/Metal) needs distinction from CPU OOM.

### 9.3 Candidate ADR: public diagnostics-bundle redaction

- **Slug:** `pipeline-bundle-redaction-transient`

**Problem.** The new `DiagnosticsBundle.transient` section will carry `Detail`, `Context` (free-form), and `StageName`. If exported for public sharing (model marketplace debug dumps, hosted support), some of that text could leak absolute paths, exception messages with file content, or system identifiers. The question is whether the new section needs its own redaction rules or inherits.

**Evidence (current state).**

- `src/Trackdub.Contracts/Diagnostics/UserProfilePathRedactor.cs:7-93` defines the existing redaction primitive (user-profile path masking).
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs:69-90, 116, 311-312` runs `RedactPaths` over each JSON artefact and byte content before writing the bundle.
- `src/Trackdub.App.Avalonia/Services/FailureDiagnosticsFormatter.cs:7-27` wraps `RedactUserProfilePaths` around `exception.Message` for the export-failed UI copy.

**Options considered.**

- (a) Inherit existing redaction. The new transient JSON is fed through `RedactPaths` like every other section; no new rules.
- (b) Add an extra redaction layer tailored to `PipelineTransientFault.Context` (paths, exception types, model ids).
- (c) Add an explicit `--share-safe` flag that runs the bundle through a more aggressive redaction pass before export.

**Trade-offs.** (a) is consistent and free; the new section ships with the same privacy posture. (b) reduces leakage of stage-specific identifiers but doubles the maintenance surface for redaction rules. (c) defers the harder call (what counts as share-safe) until a closer is signed.

**Recommendation.** (a). The exporter already iterates per section; routing the new transient JSON through the same `RedactPaths` call is one-line. Promote when share-mode is signed and the privacy surface becomes product-facing.

**Promotion criteria.** Promote when one or more of the following is true:
- The diagnostics bundle gains a real share-mode (community uploads, hosted support tunnel).
- A field is observed in the wild that escapes the current `UserProfilePathRedactor` (e.g. machine-id, GPU UUID, model manifest path).
- Compliance asks for a per-section redaction configuration rather than the existing global pass.

### 9.4 Candidate ADR: in-process Observable + upstream telemetry

> **Promotion:** see [`docs/adr/ADR-0015-pipeline-transient-telemetry.md`](../adr/ADR-0015-pipeline-transient-telemetry.md). Recommendation (a) "in-process only on on-prem tiers" was promoted on 2026-07-23 to ADR-0015; option (c) deferred to a future cloud-tier ADR.

- **Slug:** `pipeline-transient-telemetry`

Earlier drafts assumed a cloud OpenTelemetry/billing surface. Those paths were not current implementation evidence; the new AWS CPU job API does not implement that surface.

**Options considered.**

- (a) None. Observable stays in-process; per-tier consumers (Worker metrics, Avalonia VM, headless SDK) read directly.
- (b) Add an adapter interface `IPipelineTransientFaultExporter` plus one OTel implementation behind a feature flag.
- (c) No integration on the on-prem tiers; centralize in the cloud tier (API) by serializing transient-fault shipments into the existing trackdub-telemetry pipeline.

**Trade-offs.** (a) is the cheapest and matches the existing on-prem posture. (b) invites new infra on a desktop product, contradicts `AGENTS.md` §Model governance non-negotiables (“no new external telemetry surveillance on the end-user runtime path”). (c) preserves the on-prem posture and re-uses what already exists; fault shipments into the cloud tier are opt-in per project + per Tier.

**Recommendation.** (a) for the on-prem tiers shipped by this spec. Centralization, when it becomes a customer ask, belongs to (c) — the Cloud API / hosted Trackdub variant — and to a separate ADR.

**Promotion criteria.** Promote when one or more of the following is true:
- Trackdub.Cloud or a hosted customer SLI/SLO asks for per-stage transient-fault telemetry.
- A repeat user-facing incident traces to a failure pattern that surface logging alone cannot correlate.
- The Cloud tier upgrades to consume `PipelineTransientFault` directly and warrants a dedicated bridge.

## 10. References

- `docs/AGENTS.md` §Quality gates and §Verification ladder.
- `docs/architecture/P0-pipeline-audit-2026-06-01.md` — earlier pipeline failure-mode survey.
- `docs/BACKLOG.md` P0-1 closure 2026-06-10 — current "honest per-stage states" acceptance evidence.
- `src/Trackdub.Application/Dubbing/DubbingPipelineEngine.cs:1314` — existing `IsBenignSkipReasonCode`.
- `src/Trackdub.Domain/StageRuns/StageSkipReasonCodes.cs:8-29` — existing reason codes; this spec extends not replaces.
- `src/Trackdub.Application/Transcripts/StageRunHygiene.cs:13-58` — reconciliation precedent for stale-Running recovery (analog design).
- `src/Trackdub.Inference/Pool/InferenceRetryPolicy.cs:70` — ORT error-code paper-trail; this spec promotes to typed event.
- `src/Trackdub.App.Avalonia/Services/OperationRunner.cs:30-69` — lane semantics; spec extends.
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs:69-90, 116, 311-312` — section list to extend.
- `src/Trackdub.Worker/WorkerMetrics.cs:20-86` — existing `WorkerMetrics` pattern (used as template).
- `src/Trackdub.Contracts/Diagnostics/UserProfilePathRedactor.cs:7-93` — redaction primitive used by the bundle exporter (§9.3 evidence).
- `src/Trackdub.Api/Program.cs:47-49`, `src/Trackdub.Api/Observability/DubbingMetrics.cs:6`, `src/Trackdub.Api/Billing/Services/UsageMeter.cs:12` — historical assumed OTel surface, not shipped by the new AWS job API (§9.4 evidence).
- `MILESTONE.md` §Current non-negotiables — "no fake readiness / no silent failures / no silent degradation" gate.
- [`docs/adr/ADR-0015-pipeline-transient-telemetry.md`](../adr/ADR-0015-pipeline-transient-telemetry.md) — §9.4 (a) promotion: in-process `Observable` only on on-prem tiers (App/SDK/Worker/CLI); no new upstream sink. Cross-link closing the spec → ADR loop.

## 11. Validation ladder (per ADR candidate + main spec body)

Future code PRs implementing this spec, or any one ADR candidate, must satisfy the test surfaces named below. Each subsection lists required test files (suggested relative path under `tests/`), required test method names (suggested; agent may rename but must keep the same intent), required fixtures, required `dotnet test` filter, and required build TFM coverage. Conventions follow `AGENTS.md` §Verification ladder.

### 11.1 Cross-project build + test gates (apply to every change)

- `dotnet build Trackdub.sln -m:1 -p:Platform=x64` clean. `TreatWarningsAsErrors=true` is set globally per `Directory.Build.props`.
- `dotnet format --verify-no-changes` clean for every project in the diff (per `AGENTS.md` §Quality).
- Unit tests run via `dotnet test tests/<Project> --no-restore -m:1 --filter "<FullyQualifiedName>"`.
- Headless smoke runs via `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~HeadlessPipelineSmoke" $env:TRACKDUB_SMOKE_TIMEOUT_SECONDS=600` (only when a fixture + downloaded models exist on the agent).
- `dotnet test Trackdub.sln --configuration Release --no-build` is the final CI-equivalent gate. Failure there blocks the PR.

### 11.2 Main spec body — `TransientFailureKind` + bus + retry helper (§4.1–4.4)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/TransientFailureKindTests.cs`:
  - `IsTransient_returns_true_for_OperationCanceledException`
  - `IsTransient_returns_true_for_IOException_with_share_violation_hresult`
  - `IsTransient_returns_true_for_SqliteException_busy (5)`
  - `IsTransient_returns_true_for_OnnxRuntimeException_with_known_code`
  - `IsTransient_returns_true_for_HttpRequestException_5xx`
  - `IsTransient_returns_true_for_OutOfMemoryException`
  - `IsTransient_returns_false_for_ArgumentException`
  - `IsTransient_returns_false_for_NullReferenceException`
- New tests `tests/Trackdub.Application.Tests/Pipeline/PipelineTransientFaultBusTests.cs`:
  - `Publish_records_fault_in_snapshot`
  - `Publish_caps_snapshot_at_50_overflow_drops_oldest`
  - `Stream_yields_faults_in_arrival_order`
  - `UserCancellation_publishes_even_after_parent_CancellationToken_fires`
- New tests `tests/Trackdub.Application.Tests/Pipeline/StageRunWithTransientRetryTests.cs` (or co-located under `tests/Trackdub.Application.Tests/Pipeline/`):
  - `RunStageWithTransientRetry_succeeds_after_two_attempts_publishes_two_faults`
  - `RunStageWithTransientRetry_exhausts_three_attempts_rethrows`
  - `RunStageWithTransientRetry_UserCancellation_writes_Canceled_row_before_rethrow`
  - `RunStageWithTransientRetry_bubbles_non_transient_exception_unchanged`
- Fixtures: a deterministic `FakeTransientStage` (defined under `tests/Trackdub.TestDoubles/FakeTransientStage.cs` per `AGENTS.md` §Test doubles policy) that throws a controllable exception by attempt number, plus an `OperationCanceledExceptionSource` for cancel tests. Both injected through constructor parameters so production code stays untouched.
- `dotnet test tests/Trackdub.Domain.Tests --no-restore -m:1 --filter "FullyQualifiedName~TransientFailureKind"` must be 100% green.
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` must cover `TransientFaultBus` + `StageRunWithTransientRetry` + any orchestration chokepoint tests.
- TFM coverage: tests live in projects whose `<TargetFramework>` does not include `net10.0-windows10.0.19041.0`. Verify via `dotnet build tests/Trackdub.Application.Tests --no-restore`.

### 11.3 Reader surfaces (§4.5)

- New tests `tests/Trackdub.Infrastructure.Tests/Diagnostics/TransientFaultSummaryTests.cs`:
  - `MostRecent_caps_at_20_with_arrival_order`
  - `Total_matches_sum_of_countsByKind`
  - `Serialize_omits_empty_contexts`
- New tests `tests/Trackdub.Sdk.Tests/RunManifestTransientSectionTests.cs`:
  - `RunManifest_serializes_transient_section_with_integer_keys_for_kinds`
  - `RunManifest_transient_section_survives_roundtrip_through_trackdub_dub_engine`
- New tests `tests/Trackdub.Worker.Tests/WorkerTransientCounterTests.cs`:
  - `WorkerMetrics_transient_total_increments_per_fault`
  - `WorkerMetrics_transient_total_emits_to_existing_logger_path`
- New tests `tests/Trackdub.Cli.Tests/DoctorExplainTransientTests.cs`:
  - `DoctorHandler_explain_transient_user_cancellation_prints_remediation`
  - `DoctorHandler_explain_transient_directory_lock_prints_remediation`
  - `DoctorHandler_explain_transient_oom_prints_known_caveat`
- New tests `tests/Trackdub.App.Avalonia.Tests/ViewModels/PipelineRunViewModelTransientTests.cs` (Windows TFM):
  - `ApplyTransientFault_updates_LastTransientText_to_latest_kind`
  - `ApplyTransientFault_keeps_latest_when_count_exceeds_overlay_threshold`
  - `ApplyTransientFault_resets_text_when_run_succeeds`
- Fixtures: a `FakeRunManifestWriter` that captures the serialized JSON, an `InMemoryWorkerMetrics` that implements the counter-emit path, an Avalonia-friendly `FaultPublisher` mock.
- `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` covers Sdk side.
- `dotnet test tests/Trackdub.Worker.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` covers Worker side.
- Avalonia UI tests may stay on `net10.0-windows10.0.19041.0` per `AGENTS.md` §Avalonia UI verification rule.

### 11.4 ADR-CAND `pipeline-transient-aggregation` (§9.1)

- New tests `tests/Trackdub.Application.Tests/Pipeline/PipelineTransientFaultBusSnapshotPerRunTests.cs`:
  - `SnapshotPerRun_returns_only_faults_with_matching_projectId`
  - `SnapshotPerRun_groups_by_stage_then_kind_in_arrival_order`
- Optional integration test composes the existing `RunStageWithTransientRetry` fixture and asserts the rollup matches.
- Command: `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~SnapshotPerRun"`.
- Promotion gate covered when this section is exercised; spec holds the test surface as a stub so the candidate can ship without further discovery.

### 11.5 ADR-CAND `pipeline-oom-classification` (§9.2)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/OomClassifierTests.cs` (only land if the candidate is promoted):
  - `Heap_ratio_over_threshold_returns_MemoryExhausted_kind`
  - `Cpu_memory_pressure_score_above_band_returns_MemoryExhausted_kind`
  - `Gpu_runtime_memory_event_maps_to_distinct_oom_subkind`
- These tests are stubs in this spec; they only ship together with the candidate. Before promotion, no test code is required.
- Promotion gate: each test name above is the surface the candidate needs to satisfy.

### 11.6 ADR-CAND `pipeline-bundle-redaction-transient` (§9.3)

- New tests `tests/Trackdub.Infrastructure.Tests/Diagnostics/TransientSectionRedactionTests.cs` (only land if the candidate is promoted):
  - `RedactPaths_passes_through_transient_section_string`
  - `RedactPaths_does_not_mutate_unredacted_fields`
  - `RedactPaths_handles_transient_section_with_no_user_profile_paths`
- These tests are stubs in this spec; they only ship together with the candidate. Before promotion, no test code is required.
- Promotion gate ensures the inheritance pattern (a) holds against regression.

### 11.7 ADR-CAND `pipeline-transient-telemetry` (§9.4)

- No tests added in this spec because the recommendation is (a), in-process only.
- Promotion gate: introduction of an opt-in exporter requires `tests/Trackdub.Api.Tests/Observability/TransientFaultExporterTests.cs` (or analog) with two surface tests:
  - `Exporter_publishes_to_existing_dubbing_metrics_counter`
  - `Exporter_does_not_initialize_when_feature_flag_disabled`
- These tests only ship when the candidate is promoted; spec keeps the surface documented to avoid re-discovery.

### 11.8 Cross-platform gates (§7)

- Avalonia UI tier builds + UI tests run on `net10.0-windows10.0.19041.0` per `AGENTS.md` TFM rule. The transient type + bus + retry helper are TFM-agnostic and live in `Trackdub.Domain` + `Trackdub.Application`.
- The classifier at `src/Trackdub.Domain/Pipeline/TransientFailureKind.IsTransient(Exception)` is platform-gated via `#if WINDOWS / #elif LINUX / #elif MACOS`. Each path has at least one unit test:
  - `IsTransient_Windows_share_violation_hresult_returns_true`
  - `IsTransient_Posix_EAGAIN_returns_true`
  - `IsTransient_Posix_EDEADLK_returns_true`
- `dotnet build Trackdub.Avalonia.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Cloud.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Inference.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Sdk.slnf -m:1 -p:Platform=x64` must stay clean for every change regardless of which lane is touched (per `AGENTS.md` CI gate).
- Headless smoke (`tests/Trackdub.Sdk.Tests/HeadlessPipelineSmoke`) runs the full spec under stub providers so the `RunManifest.transient` shape is observable end-to-end. Guarded by `SmokeTestFactAttribute`; skips cleanly when the smoke fixture or downloaded models are absent.

### 11.9 `StageRetryBudget` domain primitive (§4.4 retry-budget extraction)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/StageRetryBudgetTests.cs`:
  - `Default_constants_match_legacy_hardcoded_values`
  - `BackoffFor_doubles_each_attempt_until_attempt_cap` (theory)
  - `BackoffFor_caps_at_MaxBackoffMs_for_very_high_attempts`
  - `BackoffFor_clamps_calculated_value_when_MaxBackoffMs_set_below_default`
  - `Constructor_throws_for_invalid_inputs` (theory)
  - `BackoffFor_throws_when_attempt_less_than_one`
- Updated tests `tests/Trackdub.Application.Tests/StageRunHelperTests.cs`: existing retry-test sites inject a `StageRetryBudget` instead of the removed inline `StageRunHelper.TransientFailureRetryOptions`; new fact `RunStageWithTransientRetry_aborts_after_one_attempt_when_budget_max_is_one` covers a budget narrower than `Default`.
- `dotnet test tests/Trackdub.Domain.Tests --no-restore -m:1 --filter "FullyQualifiedName~StageRetryBudget"` must be 100% green.
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~RunStageWithTransientRetry"` must cover both the `Default`-budget path and the injected-budget path.
- `StageRetryBudget` lives in `Trackdub.Domain` (no dependencies), so future per-stage tuning can thread a custom budget through `RunStageWithTransientRetryAsync` without forking the `StageRunHelper` chokepoint.
