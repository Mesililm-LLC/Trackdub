## Model Manifest Audit

**47** models audited.

### Warnings (1)

- [musetalk-v1-5] requires_attribution=true but not found in THIRD_PARTY_NOTICES.md.


# Mitigate verified repository-wide security and quality findings

## Verified snapshot

Checked `mesalimllc/Trackdub` `main` at `d63c60eb4a7693539feaa8dcfe2f5092431e96ae`.

- CodeQL: 3,256 open, 29 returned by fixed filter, 1 dismissed. All open instances reference current `main` SHA.
- Security-severity CodeQL: 30 open. 26 legitimate alert instances, 4 false positives.
- Quality CodeQL: 3,226 open severity-null recommendations, dominated by 1,964 `cs/path-combine` and 566 generic-catch alerts.
- Dependabot: 7 open, 64 fixed, 0 dismissed.
- Secret scanning: 0 open, 0 resolved.
- Security-related issues: [#430](https://github.com/mesalimllc/Trackdub/issues/430) remains open. Linked old alert is fixed, but equivalent current alert #20 survives.

## Legitimate open findings

### Security CodeQL

- High, `actions/untrusted-checkout/high`: privileged Tessl job executes same-repository PR code after using secrets and with `pull-requests: write`. `.github/workflows/tessl-trackdub-review.yml:82-90`, [alert 20](https://github.com/mesalimllc/Trackdub/security/code-scanning/20), open.
- Medium, `actions/missing-workflow-permissions`: release jobs inherit repository defaults at `.github/workflows/release.yml:13,36,67,103`, [5](https://github.com/mesalimllc/Trackdub/security/code-scanning/5), [6](https://github.com/mesalimllc/Trackdub/security/code-scanning/6), [8](https://github.com/mesalimllc/Trackdub/security/code-scanning/8), [9](https://github.com/mesalimllc/Trackdub/security/code-scanning/9); TRT smoke inherits defaults at `.github/workflows/trt-rtx-smoke.yml:12`, [7](https://github.com/mesalimllc/Trackdub/security/code-scanning/7). All open.
- Medium, `cs/log-forging`: request path/method and exception messages can contain line breaks under simple console logging. `ExceptionHandlerMiddleware.cs:77,82`, [15](https://github.com/mesalimllc/Trackdub/security/code-scanning/15), [16](https://github.com/mesalimllc/Trackdub/security/code-scanning/16), [17](https://github.com/mesalimllc/Trackdub/security/code-scanning/17), [18](https://github.com/mesalimllc/Trackdub/security/code-scanning/18). Open.
- Medium, `actions/unpinned-tag`: 16 movable third-party action references. Open alerts:
  - `api-deploy.yml:30,37,40,43,85,92`: [21](https://github.com/mesalimllc/Trackdub/security/code-scanning/21), [3230](https://github.com/mesalimllc/Trackdub/security/code-scanning/3230), [3231](https://github.com/mesalimllc/Trackdub/security/code-scanning/3231), [3160](https://github.com/mesalimllc/Trackdub/security/code-scanning/3160), [26](https://github.com/mesalimllc/Trackdub/security/code-scanning/26), [27](https://github.com/mesalimllc/Trackdub/security/code-scanning/27)
  - `ci.yml:27`: [25](https://github.com/mesalimllc/Trackdub/security/code-scanning/25)
  - `code-coverage.yml:74,86`: [28](https://github.com/mesalimllc/Trackdub/security/code-scanning/28), [3161](https://github.com/mesalimllc/Trackdub/security/code-scanning/3161)
  - `frontend-build.yml:18`: [31](https://github.com/mesalimllc/Trackdub/security/code-scanning/31)
  - `model-audit.yml:18`: [32](https://github.com/mesalimllc/Trackdub/security/code-scanning/32)
  - `opencode.yml:40`, `opencode-review.yml:61`: [33](https://github.com/mesalimllc/Trackdub/security/code-scanning/33), [34](https://github.com/mesalimllc/Trackdub/security/code-scanning/34)
  - `release.yml:111`: [35](https://github.com/mesalimllc/Trackdub/security/code-scanning/35)
  - `tessl-trackdub-review.yml:91`: [36](https://github.com/mesalimllc/Trackdub/security/code-scanning/36)
  - `dependabot-auto-merge.yml:19`: [3159](https://github.com/mesalimllc/Trackdub/security/code-scanning/3159)

### Dependabot

GitHub supplies manifest, not source line. Current lock lines added from `main`.

- Low, `GHSA-866g-f22w-33x8`: `@ai-sdk/provider-utils` 3.0.25, `package-lock.json:119`, [alert 48](https://github.com/mesalimllc/Trackdub/security/dependabot/48). Dev-tool response resource consumption; no patched release.
- High, `GHSA-hmw2-7cc7-3qxx`: `form-data` 4.0.5, `package-lock.json:937`, [alert 49](https://github.com/mesalimllc/Trackdub/security/dependabot/49). Patched in 4.0.6.
- Hono 4.12.23, `package-lock.json:1099`, patched in 4.12.25:
  - Medium `GHSA-j6c9-x7qj-28xf`, [50](https://github.com/mesalimllc/Trackdub/security/dependabot/50)
  - Medium `GHSA-wwfh-h76j-fc44`, [51](https://github.com/mesalimllc/Trackdub/security/dependabot/51)
  - High `GHSA-88fw-hqm2-52qc`, [52](https://github.com/mesalimllc/Trackdub/security/dependabot/52)
  - Medium `GHSA-wgpf-jwqj-8h8p`, [53](https://github.com/mesalimllc/Trackdub/security/dependabot/53)
  - Medium `GHSA-rv63-4mwf-qqc2`, [54](https://github.com/mesalimllc/Trackdub/security/dependabot/54)

Hono and `form-data` vulnerable functions are not reachable from shipped Trackdub code. They enter through unreferenced root developer packages. Alerts remain legitimate dependency-hygiene findings, not product exploit paths.

### Confirmed quality defects

- `cs/local-not-disposed`: undisposed ONNX `RunOptions`, `LanguageModel.cs:209`, [65](https://github.com/mesalimllc/Trackdub/security/code-scanning/65).
- `cs/loss-of-precision`: integer multiplication may overflow before conversion, `CosyVoiceLengthRegulator.cs:136`, [2976](https://github.com/mesalimllc/Trackdub/security/code-scanning/2976).
- `cs/cast-from-abstract-to-concrete-collection`: brittle `IReadOnlyList` to `List` cast, `RuntimePlanner.cs:403`, [280](https://github.com/mesalimllc/Trackdub/security/code-scanning/280).
- `cs/dispose-not-called-on-throw`: first backend disposal can prevent second cleanup, `PlaybackAbstractions.cs:520-521`, [52](https://github.com/mesalimllc/Trackdub/security/code-scanning/52), [53](https://github.com/mesalimllc/Trackdub/security/code-scanning/53).
- `cs/dispose-not-called-on-throw`: redundant `Close` can bypass `Dispose`, `ProjectLock.cs:137`, [54](https://github.com/mesalimllc/Trackdub/security/code-scanning/54).

## False positives and historical state

- False positive, `actions/untrusted-checkout-toctou/high`: checkout uses immutable resolved `headRefOid`, so no ref TOCTOU. [Alert 19](https://github.com/mesalimllc/Trackdub/security/code-scanning/19).
- Intended fixed-boundary review publishing, not arbitrary exfiltration/write: [alerts 45](https://github.com/mesalimllc/Trackdub/security/code-scanning/45) and [46](https://github.com/mesalimllc/Trackdub/security/code-scanning/46).
- Generated MSW service worker validates controlled client ID; browser scope supplies origin boundary. `mockServiceWorker.js:23`, [alert 47](https://github.com/mesalimllc/Trackdub/security/code-scanning/47).
- Quality false positives include localized runtime format strings [63](https://github.com/mesalimllc/Trackdub/security/code-scanning/63), conditionally formatted fixed model paths [64](https://github.com/mesalimllc/Trackdub/security/code-scanning/64), explicit session ownership transfer [49](https://github.com/mesalimllc/Trackdub/security/code-scanning/49)-[51](https://github.com/mesalimllc/Trackdub/security/code-scanning/51), and intentional process-global TRT registration [2977](https://github.com/mesalimllc/Trackdub/security/code-scanning/2977).
- Fixed CodeQL security findings: cache poisoning [2](https://github.com/mesalimllc/Trackdub/security/code-scanning/2), prior privileged checkouts [3](https://github.com/mesalimllc/Trackdub/security/code-scanning/3), [4](https://github.com/mesalimllc/Trackdub/security/code-scanning/4), prior action pins [22](https://github.com/mesalimllc/Trackdub/security/code-scanning/22)-[24](https://github.com/mesalimllc/Trackdub/security/code-scanning/24), [29](https://github.com/mesalimllc/Trackdub/security/code-scanning/29), [30](https://github.com/mesalimllc/Trackdub/security/code-scanning/30).
- Fixed CodeQL quality findings: 11 path-combine, 8 generic-catch, 1 LINQ recommendation. Remaining fixed-filter result overlaps dismissed alert.
- Dismissed: medium `actions/missing-workflow-permissions`, `.github/workflows/windows-build.yml:11`, false positive, [alert 1](https://github.com/mesalimllc/Trackdub/security/code-scanning/1).
- Dependabot fixed: 64 alerts across root `package-lock.json`, frontend lock, cursor-tool lock, and API project. No dismissed alerts.
- Secret scanning: no current or historical findings.

## Implementation changes

- Split Tessl workflow into unprivileged analysis job and trusted publishing job. PR code receives no secrets or write token; publishing job consumes data artifact and runs only script checked out from trusted workflow SHA.
- Add top-level `contents: read`; grant `contents: write` only to release-publishing job. Keep TRT workflow read-only.
- Pin every external action to current immutable SHA, retaining version comments. Include Graphite `main`, OpenCode `latest`, Tessl, AWS, Docker, coverage, release, and Dependabot actions.
- Normalize CR/LF in attacker-influenced log fields before structured logging; keep original exception object for stack trace.
- Remove unreferenced root `@supermemory/tools` and `@modelcontextprotocol/server-everything` dependencies. Regenerate `package-lock.json` and `bun.lock`, closing all seven open Dependabot alerts without shipping unused developer servers.
- Fix five confirmed quality roots: dispose `RunOptions`; cast before multiplication; use `List<DeviceEntry>` dictionary values; guarantee both playback backends are cleaned independently; replace `Close` plus `Dispose` with one reliable disposal path.
- Change canonical CodeQL workflow from redundant `security-extended,security-and-quality` to `security-extended`. Keep compiler warnings-as-errors, format verification, analyzers, and tests as quality gates. This removes security-dashboard landfill while preserving broader security queries.
- Dismiss four verified security false positives with evidence comments. Update issue #430 to current alert #20, then close after workflow rerun proves resolution.

## Interfaces and tests

- No public application API or schema changes.
- Add unit tests for CR/LF log normalization, large CosyVoice interpolation dimensions, ONNX `RunOptions` disposal, backend cleanup when first disposal throws, and project-lock cleanup.
- Validate workflow YAML, permission boundaries, immutable action references, fork/same-repository PR behavior, release publishing, and Tessl artifact handoff.
- Run focused inference, media-playback, SDK, API, and workflow checks; then full Release build/test.
- Acceptance: current high/medium legitimate CodeQL alerts close, seven Dependabot alerts close, secret scanning stays empty, false positives carry documented dismissals, issue #430 closes, and no new security alert replaces fixed instances.

# Full Pipeline Walk + Audit

**Date:** 2026-07-08  
**Scope:** End-to-end dubbing pipeline in Trackdub (SDK + Application + Composition + Domain)  
**Focus:** Structure, execution order, mechanisms (readiness, snapshots, artifacts, resume, failure), weak points, bottlenecks, breakage risks, bad configurations, and recommended fixes.  

All paths respect core invariants:
- Never fake readiness (distinct provider/model/artifact/stage states).
- Preserve original + prior usable artifacts on skip/failure.
- Strict layering (Domain → Contracts → Application → ...).

## Canonical Stage Inventory

From `src/Trackdub.Domain/StageRuns/StageNames.cs`:

- `separation`, `speech-enhancement`, `audio-preparation`
- `vad`, `diarization`, `asr`, `text-refinement-asr`, `text-refinement-translation`, `text-refinement`
- `speaker-assignment`
- `translation`
- `tts`, `voice-cloning`
- `overlap-rescue`
- `preview-mix`
- `lip-sync`, `lip-synthesis`
- `export`

**High-level SDK "DefaultStageOrder"** (`src/Trackdub.Sdk/TrackdubDubbingEngine.cs`):
```csharp
[ Separation, Vad, Diarization, Asr, Translation, Tts, Export ]
```
(Note explicit comment: Diarization before ASR so speaker labels are available when `SpeakerAssignmentAndPersistenceStage` persists the transcript.)

Many stages are granular and only appear inside the transcript sub-pipeline or as optional/side paths.

## Execution Layers

1. **Outer orchestrator** (SDK/CLI/unattended):
   - `TrackdubDubbingEngine.ExecuteAsync` (`src/Trackdub.Sdk/TrackdubDubbingEngine.cs`)
   - Source media validation (with fallback to stored path from project context)
   - Snapshot capture
   - Preflight checks
   - Loop over resolved stage order
   - Resume skip + prerequisite blocking + dispatch to `RunStageWorkflowAsync`

2. **Workspace / Workflow layer**:
   - `TranscriptWorkspace` (holds all workflows + `_pipelineGuard` SemaphoreSlim for serialization)
   - `ProjectWorkflow`, `TranslationWorkflow`, `TtsWorkflow`, `TtsOrchestrationService`, `ExportWorkflow`, `PreviewMixWorkflow`, `LipSyncWorkflow?`, `LipSynthesisWorkflow?` (latter two optional/nullable)
   - `RunInitialTranscriptionAsync`, `RunTranscriptStageAsync`, `GenerateTranslationAsync`, etc.

3. **Transcript sub-pipeline** (core ASR path):
   - `TranscriptGenerationService` (`src/Trackdub.Application/Transcripts/TranscriptGenerationService.cs`)
   - Hard-coded `TranscriptPipelineBuilder` producing `TranscriptGenerationPipeline`
   - Always executes in this order for full runs:
     1. SpeechEnhancementGenerationStage
     2. VadGenerationStage
     3. SpeakerDiarizationStage
     4. AsrGenerationStage
     5. TextRefinementGenerationStage
     6. SpeakerAssignmentAndPersistenceStage
   - `ITranscriptGenerationStage.ExecuteAsync` + resume/hydration logic

4. **Leaf implementations**:
   - `*GenerationStage` (implement `ITranscriptGenerationStage`)
   - `*StageHandler` (e.g. `VadStageHandler`, `AsrStageHandler`, `StemSeparationStageHandler`, `ExportStageHandler`, `LipSyncStageHandler`)
   - Use `StageRunHelper` for consistent `StageRunRecord` lifecycle

5. **Cross-cutting**:
   - `IArtifactStore` + `TranscriptArtifactWriter`
   - `IProjectStageRunStore` + `StageRunHelper` + `StageRunHygiene`
   - `PipelineDegradationWriter`
   - `MixPlanBuilder` / `MixPlanStore`
   - Readiness: `PipelineReadinessService`, `IPipelinePreFlightChecker`, runtime planner (per `RuntimeStage`)
   - Progress: `PipelineProgressEvent`

## Detailed Flow (Happy Path)

**Prep / Ingestion** (`ProjectWorkflow`):
- Create project / media spine
- `EnsureNormalizedProjectAudioAsync` (FFmpeg)
- Optional stem separation (`RunStemSeparationAsync` → `StemSeparationStageHandler`): produces vocals/ambiance stems + speech-enhancement as side effect (with fallback to unenhanced)
- Speech audio routing plan (prefers vocal stem for downstream VAD/ASR)

**Transcript Sub-Pipeline** (sequential):
- Speech enhancement
- VAD → `SpeechRegions` artifact (via `VadStageHandler`)
- Diarization (if enabled)
- ASR (on regions) → raw segments. Special case: VAD finds zero regions → explicit skip + `PipelineDegradationRecord("VAD_NO_REGIONS")`
- Text refinement (ASR)
- Speaker assignment + persistence → `TranscriptRevision` saved

**Post-ASR**:
- Translation (segment level)
- TTS (per-speaker, multiple candidate takes, timing)
- Optional overlap rescue

**Mix / Lip / Export**:
- `MixPlanBuilder`: selects takes (newest + user-selected candidate first), incorporates lip-sync clips when present, emits `MixPlanWarning`s for gaps/missing/stale/LipSyncArtifactMissing
- Preview mix (separate workflow for UI scrubbing)
- Lip-sync / Lip-synthesis (optional, video-frame based, heavy models, not part of default SDK order)
- Export (`ExportStageHandler`): builds final mix plan, loudness normalization, render, optional video recompose, subtitles, mux. Writes export artifacts.

All prior usable artifacts are left in place on skips/failures.

## Key Mechanisms

**Resume / Skip**:
- `DubbingEngine` + `TranscriptGenerationPipeline.ShouldResumeSkipStage`
- Checks: `!ForceRerun`, project state, artifacts exist, `StageArtifactResumeEvaluator.CanResumeStage(...)` using execution snapshot
- Special skips for disabled diarization / text-refinement
- On skip: writes `StageRunRecord` as Skipped + hydrates context from prior artifacts

**Snapshots**:
- Captured early (`CaptureExecutionSnapshot`)
- Includes model aliases, prefs, languages, `EnableAsrTextRefinement`, export format, fingerprints
- Used for both resume decisions and provenance

**Stage Run Records**:
- `StageRunHelper.StartAsync` + `RunStageAsync` / `SkipAsync` / `CompleteAsync`
- Every known stage name has an explicit case (prevents typos)
- Statuses are distinct (Running, Succeeded, Skipped, Failed, Canceled)

**Failure / Degradation**:
- Prerequisite stages (currently only Vad + Asr) block subsequent stages in outer loop
- Unhandled exceptions → degradation record (best-effort) + rethrow
- Speech enhancement failure inside separation is specially extracted and reported
- Empty-region ASR case explicitly degrades instead of pretending success

**Preflight / Readiness / Hardware**:
- Per-stage runtime planning (execution provider selection)
- Model provisioning can be deferred into stage in some cases
- Separate from "stage ran" state

## Identified Weakpoints, Bottlenecks, Breakage Risks, Bad Configs

1. **Lips not executed in main SDK pipeline** (`src/Trackdub.Sdk/TrackdubDubbingEngine.cs:665` switch + `RunStageWorkflowAsync`)
   - `DefaultStageOrder` and dispatch only cover Separation/Vad/Diarization/Asr/Translation/Tts/Export.
   - Lips are prepared in `CreateRuntimeSelectionsAsync` and snapshot (lines ~874, 942) but never dispatched.
   - `LipSyncWorkflow` / `LipSynthesisWorkflow` are nullable in `TranscriptWorkspace`.
   - **Breakage**: "Full pipeline" via SDK/CLI produces no lip-synced video. UI has exposure (options, segment provenance) but inconsistent surface.
   - Related: `docs/internal/pipeline-arch-audit.md` and architecture audits hint at this gap historically.

2. **Prerequisite blocking is too narrow** (`src/Trackdub.Sdk/TrackdubDubbingEngine.cs:40`)
   - Only `Vad` + `Asr` block later stages.
   - Diarization failure allows Translation/TTS/Export to proceed (bad or missing speaker labels).
   - **Risk**: Silent degradation of output quality.

3. **Resume logic is duplicated and fragile**
   - Spread across `DubbingEngine.HasValidExistingArtifactsAsync`, `TranscriptGenerationPipeline.ShouldResumeSkipStage`, `StageArtifactResumeEvaluator`, snapshot equality checks.
   - Snapshot includes model prefs; changing a model alias can cause wrong skip or unnecessary rerun.
   - Speaker assignment hydrate depends on prior ASR + diarization artifacts being present.
   - **Bottleneck + correctness risk** on iterative work.

4. **Purely sequential execution**
   - `TranscriptGenerationPipeline` is a simple `foreach`.
   - TTS (per speaker), ASR (per region) are serial.
   - **Major bottleneck** for long-form content or many speakers.
   - No use of parallelism for independent post-diarization work.

5. **Speech enhancement ownership is confused**
   - Runs as internal side-effect inside `StemSeparationStageHandler`.
   - Also appears as first `SpeechEnhancementGenerationStage` in the transcript builder.
   - Degradation extraction is special-cased only for the separation path.
   - Enablement and timing relative to VAD are split across flags and call sites.

6. **Mixing is buried inside Export** (`ExportStageHandler`, `MixPlanBuilder`)
   - No top-level `preview-mix` or `mix` stage in `DefaultStageOrder`.
   - `MixPlanBuilder` has complex take selection + many warning codes (MissingTake, StaleTake, LipSyncArtifactMissing, etc.).
   - Silent gaps are produced; warnings are collected but not strongly enforced before export.
   - Preview mix is a completely separate named stage/workflow.

7. **Stage graph is fragmented and stringly-typed**
   - Hard-coded array in SDK, hard-coded builder list in `TranscriptGenerationService`, switch in `ResolveTranscriptStages`, another switch in `StageRunHelper.StartKnownStageRun`, dispatch switch in DubbingEngine.
   - Coarse stage names in SDK vs. fine-grained internal stages.
   - Enable flags (`enableStemSeparation`, `enableSpeakerDiarization`, `EnableAsrTextRefinement`) not applied uniformly between SDK and shell.

8. **Lip stages are second-class / optional**
   - Depend on video extractors/recomposers + specific commercial models.
   - Not automatically included even when video source + model prefs are present.
   - Test fixtures are env-var gated.

9. **Cancellation and resource hygiene**
   - Cancellation works between outer stages but long-running inner ONNX/FFmpeg work may not be promptly interruptible.
   - Stem temp directories cleaned only at handler start.
   - Single semaphore serializes all pipeline activity per workspace.

10. **Model provisioning / preflight is scattered**
    - Multiple entry points (`EnsureImportModelsForTranscriptionAsync`, per-stage ensure, `RuntimeModelSetupWorkflow`, deferred download inside some handlers).
    - Risk of inconsistent readiness state.

11. **Edge cases around empty / failed early stages**
    - ASR handles VAD-empty gracefully.
    - Downstream stages (assignment, TTS, mix plan, export) have varying levels of defensive handling.
    - Export can be reached with incomplete prior artifacts.

12. **Other observations**
    - `StageNames` is the single source of truth (good), but many call sites still risk typos before compile-time enforcement.
    - Degradation writes are best-effort.
    - FFmpeg and ONNX session creation are repeated hot paths without obvious high-level pooling visible from Application layer.
    - Config defaults live in various `*Defaults.cs` files; no single pipeline policy object.

## Recommended Fixes (Prioritized)

**High**
- Add lipsync/lipsynthesis (conditionally) to `DefaultStageOrder` and the dispatch switch in `TrackdubDubbingEngine`. Make execution of `Lip*Workflow` part of a full run when applicable.
- Expand `PrerequisiteStages` to include `Diarization` (and consider `Translation`).
- Centralize the stage graph definition (one list + metadata for prerequisites, artifact contracts, resume eligibility). Drive builder, dispatcher, CLI filters, and UI from it.

**Medium**
- Unify and harden resume logic. Add a versioned snapshot hash + explicit "invalidate stage" API. Add comprehensive tests for snapshot drift.
- Promote an explicit `Mix` stage (or make mix plan generation a named stage that export consumes).
- Make speech enhancement a first-class, independently configurable pre-VAD stage with uniform degradation reporting.
- Introduce bounded parallelism for TTS generation across speakers and (where safe) segment batches inside ASR/translation.

**Lower / Hygiene**
- Strengthen guards in assignment/TTS/mix/export for missing prior artifacts (fail fast or produce clear degradation).
- Improve cooperative cancellation inside long-running engines and add hard timeouts with cleanup.
- Centralize model provisioning gate so every stage path goes through the same readiness check.
- Add integration-level test that runs full SDK order + resume + injected failures + lips (when fixtures available).
- Audit every stage handler for "write artifact before marking success" and "preserve prior artifacts on exception".

## References (key files walked)

- `src/Trackdub.Sdk/TrackdubDubbingEngine.cs`
- `src/Trackdub.Sdk/TrackdubPipelineStages.cs`
- `src/Trackdub.Domain/StageRuns/StageNames.cs`
- `src/Trackdub.Application/Transcripts/Transcript{Workspace,GenerationService,ProjectWorkflow}.cs`
- `src/Trackdub.Application/Transcripts/Pipeline/TranscriptGenerationPipeline.cs` + `TranscriptPipelineBuilder.cs`
- `src/Trackdub.Application/Transcripts/Stages/*.cs` (all 6)
- `src/Trackdub.Application/Transcripts/*StageHandler.cs` (StemSeparation, Export, etc.)
- `src/Trackdub.Application/Mixing/MixPlanBuilder.cs`
- `src/Trackdub.Application/Transcripts/StageRunHelper.cs`
- `src/Trackdub.Composition/CompositionRoot.cs` (registrations)
- `src/Trackdub.Application/Pipeline/README.md` + related preflight

Related prior audits:
- `docs/architecture/P0-pipeline-audit-2026-06-01.md`
- `docs/internal/pipeline-arch-audit.md`

---

**End of audit.** This document captures the pipeline as exercised through SDK entry points, the internal transcript sub-pipeline, mixing, export, and optional lip paths. Further deep dives can be performed on any specific stage or mechanism. 

To regenerate or extend: run the same exploration against current sources and update this file.

# Dead Code Audit Review: Mistakes & Resolutions

**Source:** `Trackdub-dead-code-audit-codex-refactor.md` (ReSharper `jb inspectcode` on `codex/refactor`)  
**Reviewed by:** Kiro, verified against `main` source code (July 2026)  
**Second-pass verification:** Completed (3 disputed items resolved below)

---

## Executive Summary

The audit is broadly solid (~95% accurate). The 46 "do not remove" items are all correctly categorized. However, verification against actual source reveals:

- **~80 false positives** from CommunityToolkit.Mvvm source-generator partial methods
- **~10 misclassified interface parameters** that are contractual and cannot be removed
- **1 item marked "safe" needs nuance** -- two `NullOpenVinoAvailabilityProvider` definitions exist (see section 1)
- **~10 items marked "review needed" that are confirmed dead** and can be promoted to safe
- **GlobalUsings.cs "delete" recommendation is wrong** for most files (they contain real content)
- **All 7 unused PackageVersion entries confirmed safe** to remove (verified individually)

---

## 1. CLARIFIED: NullOpenVinoAvailabilityProvider (two definitions exist)

There are **two** `NullOpenVinoAvailabilityProvider` implementations:

| # | Location | Scope | Used? |
|---|----------|-------|-------|
| 1 | `src/Trackdub.Inference/Runtime/Planning/NullOpenVinoAvailabilityProvider.cs` | `public sealed class` in `Trackdub.Inference` | YES -- used by `OnnxExecutionProviderDiscovery` default ctor and multiple test files |
| 2 | `src/Trackdub.Inference.Onnx/OnnxExecutionSessionFactory.cs:1657` | `private sealed class` nested inside `OnnxExecutionSessionFactory` | YES -- used on line 1651 (`NullOpenVinoAvailabilityProvider.Instance`) in the Linux conditional |

The audit flags item #2 (the private nested class at line 1657). Despite being `private`, it IS used within `OnnxExecutionSessionFactory` itself on line 1651 under the `#elif LINUX` conditional compilation block.

**Verdict:** The audit is **wrong** to mark it safe. However, there's a design smell: two implementations of the same null-object pattern exist. Consider consolidating to only the public one in `Trackdub.Inference` and deleting the private nested duplicate.

**Action:** Reclassify as **do not remove** (or consolidate to the public version in a separate cleanup).

---

## 2. WRONG: Source-generator `value` parameters (~80 entries)

The audit flags `value` parameter as "never used" on partial methods like:

```csharp
partial void OnPlaybackVolumePercentChanged(double value) { ... }
```

These are **CommunityToolkit.Mvvm `[ObservableProperty]` hook points**. The source generator emits the partial method signature; the developer *may* choose not to use `value` in the body. The parameter cannot be removed -- it's part of the generated contract.

**Affected ViewModels (non-exhaustive):**

- `AvaloniaMainWindowViewModel.cs` (lines 555, 1318, 1323)
- `AvaloniaMainWindowViewModel.PipelineUi.cs` (line 474)
- `AvaloniaVideoFramePresenter.cs` (line 287)
- `AvaloniaTranscriptSegmentItem.cs` (lines 547, 549, 555, 561, 569)
- `ModelManagerViewModel.cs` (~18 entries)
- `PipelineStageRowViewModel.cs` (~9 entries)
- `StarterPackCardViewModel.cs` (~22 entries)
- `VoiceSpeakerCardViewModel.cs` (~6 entries)
- `WaveformTimelineViewModel.cs` (lines 30, 37, 44, 50)
- `ShellViewModel.cs` (lines 154, 160)
- `NavigatorSectionViewModel.cs`, `SegmentEditorViewModel.cs`, `DevLogViewModel.cs`, `SettingsWindowViewModel.cs`

**Action:** Remove all `value` parameter entries from both "review needed" and "safe" tables. These are not actionable. Net reduction: ~80 items from the review-needed count.

---

## 3. WRONG: Interface `CancellationToken` parameters (~11 entries)

The audit flags `ct` / `cancellationToken` parameters on interface methods in:

- `Trackdub.Contracts/IAudioPreviewTransport.cs` (6 methods)
- `Trackdub.Application/Runtime/ILicenseConsentService.cs` (2 methods)
- `Trackdub.Application/Updates/IUpdateService.cs` (3 methods)

These are **interface contract parameters**. Implementations must accept them. Removing them is a breaking API change.

**Action:** Reclassify as **do not remove** (interface contract).

---

## 4. WRONG: GlobalUsings.cs "delete if empty" recommendation

The audit recommends deleting 16 GlobalUsings.cs files as "empty or comment-only." Verification shows most contain real `global using` directives:

| File | Actual Content |
|------|----------------|
| `src/Trackdub.Application/GlobalUsings.cs` | 14 global usings (1 redundant) |
| `src/Trackdub.Composition/GlobalUsings.cs` | 9 global usings (1 redundant) |
| `src/Trackdub.Tools/GlobalUsings.cs` | 2 global usings (1 redundant) |
| `tests/Trackdub.Sdk.Tests/GlobalUsings.cs` | 2 global usings (1 redundant) |
| `tests/Trackdub.Composition.Tests/GlobalUsings.cs` | 4 global usings (1 redundant) |

**Action:** Do NOT blindly delete. Only clean the redundant using directives within them (via `dotnet format`). Re-verify the remaining 11 files individually before acting.

---

## 5. CORRECT BUT UNDER-CLASSIFIED: Items marked "review needed" that are confirmed dead

These were conservatively marked "review needed" but verification confirms they have zero consumers:

### Converters (no AXAML reference anywhere)

| Converter | File | Evidence |
|-----------|------|----------|
| `IsNotNullConverter` + `Instance` | `Converters/IsNotNullConverter.cs` | AXAML uses built-in `ObjectConverters.IsNotNull` instead |
| `StageStatusToIconConverter` + `Instance` | `Converters/StageStatusToIconConverter.cs` | Not in any `.axaml` |
| `TimeSpanToTimecodeConverter` + `Instance` | `Converters/TimeSpanToTimecodeConverter.cs` | AXAML uses `SecondsToTimecodeConverter`; this one only in a unit test |
| `VolumeToPercentConverter` + `Instance` | `Converters/VolumeToPercentConverter.cs` | Not in any `.axaml` |
| `StringToImageConverter` + `Instance` | `Converters/StringToImageConverter.cs` | Not in any `.axaml` |
| `PipelineRowAccentBrushConverter` | `Converters/PipelineRowAccentBrushConverter.cs` | Not in any `.axaml` |

### ViewModel properties with no binding

| Property | File | Evidence |
|----------|------|----------|
| `ShellViewModel.ShellStatus` | `ViewModels/ShellViewModel.cs:84` | No AXAML binding, no code-behind reference |
| `SettingsWindowViewModel.AppName` | `ViewModels/SettingsWindowViewModel.cs:402` | Property exists, no AXAML binding (BuildNumber/BuildDate/IsDevBuild ARE bound) |

### Entire dead types

| Type | File | Evidence |
|------|------|----------|
| `DeliveryContext` (+ all properties) | `WebhookDelivery/Models/DeliveryContext.cs` | Only referenced in a README. Never instantiated or deserialized. Scaffolded but unused. |
| `ModelCacheDiagnostics` | `Infrastructure/Diagnostics/ModelCacheDiagnostics.cs` | Logic duplicated as private method in `DiagnosticsCollector`. Zero callers of the static class. |
| `TextHelpers` | `App.Avalonia/Helpers/TextHelpers.cs` | Type + `TruncateWithEllipsis` never called from any `.cs` or `.axaml` |

**Action:** Promote all above to **safe to remove**.

---

## 6. CONFIRMED: Safe bulk actions

| Action | Status |
|--------|--------|
| Remove 7 unused `PackageVersion` entries from `Directory.Packages.props` | **Confirmed safe** -- individually verified, no PackageReference in any csproj (see below) |
| Remove `BuildSyntheticEvent` from `WebhookDelivery/Function.cs:95` | **Confirmed dead** -- private method, never called |
| `SessionService._sessions` is write-only | **Confirmed dead** -- collection populated but never queried. The earlier "type itself is live, used by Api DubbingOrchestrator" note was stale: `Trackdub.Api` was split into `api.trackdub` during the open-core split, and neither `api.trackdub` nor `Trackdub-gated` references `ISessionService` or `SessionService`. The gated desktop tier uses `ProjectSessionService`, a different type. `SessionService` and `ISessionService` have since been removed |
| `DubbingPipelineEngine._serviceConfigurator` | **May already be removed** -- field does not exist in current `main` (audit ran on `codex/refactor` branch) |
| `ModelCacheDiagnostics` duplication | **Confirmed** -- `DiagnosticsCollector.cs:68` has identical private `DetermineModelCacheEntry` method; static class version at `ModelCacheDiagnostics.cs:9` has zero callers |

### PackageVersion removal verification

| Package | Verified | Notes |
|---------|----------|-------|
| `Avalonia.Controls.TreeDataGrid` | Zero csproj references | Safe to remove |
| `DynamicData` | Zero csproj references | Safe to remove (appears only as transitive in dgspec) |
| `JetBrains.Annotations` | Zero csproj references | Safe to remove |
| `LibVLCSharp.Avalonia` | Zero csproj references | Safe to remove |
| `Microsoft.Graphics.Win2D` | Zero csproj references | Safe to remove |
| `OpenTelemetry.Api` | Zero csproj references | Safe to remove (only `.Extensions.Hosting`/`.Instrumentation.*` referenced) |
| `VideoLAN.LibVLC.Linux` | Zero csproj references | Safe to remove (only `.Windows` and `.Mac` variants are referenced) |

**Note:** The second-pass reviewer tested `AvaloniaUI.DiagnosticsSupport` and `Lucene.Net.Analysis.Common` which are NOT in the audit's 7-package list -- those packages ARE actively referenced and are NOT candidates for removal.

---

## 7. CONFIRMED CORRECT: "Do not remove" items

All 46 items are verified:

- FluentValidation validators: registered via `AddValidatorsFromAssemblyContaining` in `Program.cs:163`
- `SettingsTabNavigation` + `StarterPackFileDialogService`: DI-registered in `App.axaml.cs` (lines 138-139)
- `WebhookDelivery/Function.cs`: Lambda entry point with `[assembly: LambdaSerializer]` + `AWSProjectType=Lambda` in csproj
- All `Trackdub.Sdk` public surface (builders, config records, session factory): public API
- `EventBridgeEvent` / `EventEnvelope` properties: JSON serialization contracts

---

## Corrected Counts

| Category | Original | Corrected | Delta |
|----------|----------|-----------|-------|
| Do not remove | 46 | ~57 | +11 (interface params, NullOpenVino) |
| Review needed | 1156 | ~1056 | -100 (partial methods, confirmed-dead promoted out) |
| Safe to remove | 1838 | ~1928 | +90 (promoted from review needed) |
| GlobalUsings to delete | 16 | 5-6 (re-verify) | Most are NOT empty |

> **Interface-parameter component recomputed from the tree.** The surviving interfaces carry 11 such parameters (`IAudioPreviewTransport` 6, `ILicenseConsentService` 2, `IUpdateService` 3). The original `+10` predated the removal of `ITranslationService` (1) and `IVoiceAssignmentService` (2), and undercounted `IAudioPreviewTransport`, which has 6 methods rather than the 5 the audit claimed.

---

## Recommended Execution Order

1. **`dotnet format --no-restore`** -- cleans all redundant using directives (largest safe category, ~1000 items)
2. **Remove 7 unused PackageVersion entries** from `Directory.Packages.props`
3. **Delete confirmed-dead files:**
   - `src/Trackdub.App.Avalonia/Converters/IsNotNullConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/StageStatusToIconConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/TimeSpanToTimecodeConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/VolumeToPercentConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/StringToImageConverter.cs`
   - `src/Trackdub.App.Avalonia/Converters/PipelineRowAccentBrushConverter.cs`
   - `src/Trackdub.App.Avalonia/Helpers/TextHelpers.cs`
   - `src/Trackdub.Infrastructure/Diagnostics/ModelCacheDiagnostics.cs`
   - `src/Trackdub.WebhookDelivery/Models/DeliveryContext.cs`
4. **Remove dead members:**
   - `ShellViewModel.ShellStatus`
   - `SettingsWindowViewModel.AppName`
   - `Function.BuildSyntheticEvent`
5. **Do NOT touch:**
   - Source-generator partial method `value` parameters
   - Interface `CancellationToken` parameters
   - `NullOpenVinoAvailabilityProvider`
   - GlobalUsings.cs files (run `dotnet format` on them instead)
6. **Build + test:** `dotnet build Trackdub.sln -m:1 -p:Platform=x64 -warnaserror && dotnet test Trackdub.sln -m:1 -p:Platform=x64`

# Dead Code Audit Review: Verification Against Trackdub Repo

**Date:** 2026-07-13  
**Verified by:** AI agent (Pi)  
**Source document:** `Trackdub-dead-code-audit-review.md`  
**Verification method:** Static analysis of C# source code, AXAML files, `.csproj` files, and `Directory.Packages.props`

---

## Executive Summary

The audit review document is **highly accurate** (95%+). Most claims verified correctly against the repository. A few minor inaccuracies found:

1. **`NullOpenVinoAvailabilityProvider` location clarification needed** - Review says audit marked it "safe to remove" but actual code shows it's used
2. **`JetBrains.Annotations` NOT unused** - The review missed that `JetBrains.Annotations` has zero `.csproj` references (likely unused)
3. **All converter dead code claims CONFIRMED** - 6 converters verified as having zero AXAML bindings
4. **All "confirmed dead" items VERIFIED** - `DeliveryContext`, `ModelCacheDiagnostics`, `TextHelpers` are all unused
5. **`GlobalUsings.cs` claim CONFIRMED** - Most files contain real `global using` directives (should NOT be blindly deleted)

---

## Detailed Verification Results

### 1. WRONG: `NullOpenVinoAvailabilityProvider` (Section 1)

**Review claim:** Audit marked `NullOpenVinoAvailabilityProvider` as "safe to remove" but it's actually used.

**Verification Result: PARTIALLY WRONG**

Two different `NullOpenVinoAvailabilityProvider` exist:

1. **Local class in `OnnxExecutionSessionFactory.cs:1657`** - Used only within that file (line 1651). If the audit was flagging THIS one, it might actually be unused outside that file.

2. **Separate file `Inference/Runtime/Planning/NullOpenVinoAvailabilityProvider.cs`** - Actively used in:
   - `OnnxExecutionProviderDiscovery` constructor (line 27)
   - Unit tests in `Trackdub.Inference.Tests`

**Action:** The review is correct that this type should NOT be removed, but the **location in the review is ambiguous**. The audit might have been flagging the local class (which could be dead), not the separate file.

**Verdict:** Review correctly says "do not remove" but the **evidence citation is unclear**.

---

### 2. WRONG: Source-generator `value` parameters (~80 entries) (Section 2)

**Review claim:** These are false positives from CommunityToolkit.MVVM source-generator partial methods. The `value` parameter cannot be removed.

**Verification Result: CONFIRMED CORRECT**

**Evidence:**
- `AvaloniaMainWindowViewModel.cs:1318` shows:
  ```csharp
  partial void OnPlaybackVolumePercentChanged(double value)
  {
      _ = ApplyPlaybackVolumeAsync();
  }
  ```
- This is a CommunityToolkit.MVVM `[ObservableProperty]` hook. The source generator EMITS the partial method signature with `value` parameter. Even if the implementation doesn't use `value`, the parameter is part of the contract.

**Verdict:** Review is correct. These should be removed from the "safe to remove" list.

---

### 3. WRONG: Interface `CancellationToken` parameters (~11 entries) (Section 3)

**Review claim:** These are interface contract parameters and should not be removed.

**Verification Result: CONFIRMED CORRECT**

**Evidence:**
- `Trackdub.Contracts/IAudioPreviewTransport.cs` shows:
  ```csharp
  public interface IAudioPreviewTransport : IDisposable
  {
      Task OpenAsync(string absoluteFilePath, CancellationToken ct);
      Task PlayAsync(CancellationToken ct);
      // ...
  }
  ```
- These are interface definitions. Removing `CancellationToken` would be a breaking API change.

**Verdict:** Review is correct. These are contractual and should be reclassified as "do not remove".

---

### 4. WRONG: `GlobalUsings.cs` "delete if empty" recommendation (Section 4)

**Review claim:** The audit recommends deleting 16 `GlobalUsings.cs` files as "empty or comment-only," but most contain real `global using` directives.

**Verification Result: CONFIRMED CORRECT**

**Evidence:**
- `src/Trackdub.Application/GlobalUsings.cs` contains:
  ```csharp
  global using Trackdub.Application.ModelOptimization;
  global using Trackdub.Application.Projects;
  // ... 12 more usings
  global using TranscriptSegment = Trackdub.Domain.Transcript.TranscriptSegment;
  ```
- `src/Trackdub.Composition/GlobalUsings.cs` contains 9 real `global using` directives.

**Verdict:** Review is correct. These files should NOT be blindly deleted. Run `dotnet format` to clean redundant usings instead.

---

### 5. CORRECT BUT UNDER-CLASSIFIED: Items marked "review needed" that are confirmed dead (Section 5)

#### 5.1 Converters with no AXAML reference

**Review claim:** 6 converters are dead code (not referenced in any `.axaml` file).

**Verification Result: CONFIRMED CORRECT**

**Evidence (all verified with `grep -r` in `.axaml` files):**

| Converter | File | AXAML References | Status |
|-----------|------|-------------------|--------|
| `IsNotNullConverter` | `Converters/IsNotNullConverter.cs` | **0** | DEAD |
| `StageStatusToIconConverter` | `Converters/StageStatusToIconConverter.cs` | **0** | DEAD |
| `TimeSpanToTimecodeConverter` | `Converters/TimeSpanToTimecodeConverter.cs` | **0** | DEAD |
| `VolumeToPercentConverter` | `Converters/VolumeToPercentConverter.cs` | **0** | DEAD |
| `StringToImageConverter` | `Converters/StringToImageConverter.cs` | **0** | DEAD |
| `PipelineRowAccentBrushConverter` | `Converters/PipelineRowAccentBrushConverter.cs` | **0** | DEAD |

**Note:** The review correctly points out that AXAML files use built-in `ObjectConverters.IsNotNull` instead of the custom `IsNotNullConverter`.

**Verdict:** All 6 converters are confirmed dead. Safe to remove.

---

#### 5.2 ViewModel properties with no binding

**Review claim:** `ShellViewModel.ShellStatus` and `SettingsWindowViewModel.AppName` are not bound in AXAML.

**Verification Result: CONFIRMED CORRECT**

**Evidence:**

1. **`ShellViewModel.ShellStatus`** (line 84):
   ```csharp
   public string ShellStatus =>
       "Avalonia shell wired to real Trackdub project/media state. Linux/macOS ML providers remain deferred behind CPU fallback.";
   ```
   - Grep for `ShellStatus` in `.axaml` files: **0 results**
   - Grep for `ShellStatus` in `.cs` files (outside ViewModel): **0 results**
   - **CONFIRMED DEAD**

2. **`SettingsWindowViewModel.AppName`** (line 402):
   ```csharp
   public string AppName { get; } =
       ResolveBuildMetadataFromAssemblyOrEnvironment(AppNameDefault, null, AppNameMetadataKey);
   ```
   - Grep for `AppName` in `SettingsWindow.axaml`: **0 results** (only `BuildNumber` and `BuildDate` are bound)
   - **CONFIRMED DEAD**

**Verdict:** Both properties are dead code. Safe to remove.

---

#### 5.3 Entire dead types

**Review claim:** `DeliveryContext`, `ModelCacheDiagnostics`, and `TextHelpers` are entirely unused.

**Verification Result: CONFIRMED CORRECT**

**Evidence:**

1. **`DeliveryContext`** (`WebhookDelivery/Models/DeliveryContext.cs`):
   - Grep for `DeliveryContext` in `.cs` files: **Only found in its own definition**
   - Not instantiated anywhere
   - Not deserialized
   - **CONFIRMED DEAD**

2. **`ModelCacheDiagnostics`** (`Infrastructure/Diagnostics/ModelCacheDiagnostics.cs`):
   - Grep for `ModelCacheDiagnostics` in `.cs` files: **Only found in its own definition**
   - The review claims logic is duplicated in `DiagnosticsCollector` (needs verification, but zero callers confirmed)
   - **CONFIRMED DEAD**

3. **`TextHelpers`** (`App.Avalonia/Helpers/TextHelpers.cs`):
   - Grep for `TextHelpers` in `.cs` and `.axaml` files: **Only found in its own definition**
   - `TruncateWithEllipsis` method never called
   - **CONFIRMED DEAD**

**Verdict:** All 3 types are dead code. Safe to remove.

---

### 6. CONFIRMED: Safe bulk actions (Section 6)

**Review claim:** 7 unused `PackageVersion` entries in `Directory.Packages.props` are safe to remove.

**Verification Result: PARTIALLY VERIFIED (needs full audit)**

**Evidence (sample checks):**

1. **`JetBrains.Annotations`** (line 24 in `Directory.Packages.props`):
   - Grep for `JetBrains.Annotations` in `.csproj` files: **0 results**
   - **CONFIRMED UNUSED** ✓

2. **`AvaloniaUI.DiagnosticsSupport`** (line 13):
   - Referenced in `Trackdub.App.Avalonia.csproj` (line 88) and `DubBench.csproj` (line 40)
   - **IN USE** ✗

3. **`Lucene.Net.Analysis.Common`** (line 25):
   - Referenced in `Trackdub.Infrastructure.csproj` (line 21)
   - **IN USE** ✗

**Verdict:** The claim of "7 unused entries" needs a **full audit** of ALL `PackageVersion` entries. The review didn't provide the list of 7 packages. Sample check shows at least 2 have references.

**Recommendation:** Do NOT blindly remove 7 entries. Verify each one with:
```bash
grep -r "<PackageReference Include=\"PACKAGE_NAME\"" **/*.csproj
```

---

### 7. CONFIRMED CORRECT: "Do not remove" items (Section 7)

**Review claim:** All 46 "do not remove" items are verified as correct.

**Verification Result: NOT VERIFIED (too many to check manually)**

The review lists validators, DI-registered services, Lambda entry points, public SDK surfaces, and JSON serialization contracts as "do not remove." These are **architecturally correct** claims that require manual verification of:

- `Program.cs:163` for `AddValidatorsFromAssemblyContaining`
- `App.axaml.cs:138-139` for DI registration
- `[assembly: LambdaSerializer]` attribute
- Public API surface of `Trackdub.Sdk`

**Verdict:** Assume correct pending full manual verification.

---

## Corrected Counts (from Review Section "Corrected Counts")

The review provides corrected counts:

| Category | Original | Corrected | Delta |
|----------|----------|-----------|-------|
| Do not remove | 46 | ~57 | +11 |
| Review needed | 1156 | ~1056 | -100 |
| Safe to remove | 1838 | ~1928 | +90 |

**Verdict:** These adjusted counts appear reasonable based on the verified corrections.

---

## Recommended Execution Order (Review Section 7)

The review recommends this execution order:

1. **`dotnet format --no-restore`** - Cleans redundant using directives
2. **Remove 7 unused `PackageVersion` entries** - **WARNING: Verify each package first!**
3. **Delete confirmed-dead files** - 9 files listed
4. **Remove dead members** - 4 items listed
5. **Do NOT touch** - Source-generator params, interface params, `NullOpenVinoAvailabilityProvider`, `GlobalUsings.cs`
6. **Build + test** - Verify changes

**Verdict:** This is a **sound execution plan**, but step 2 needs verification (see Section 6 above).

---

## Accuracy Assessment

| Section | Accuracy | Notes |
|----------|----------|-------|
| Section 1 (`NullOpenVinoAvailabilityProvider`) | 90% | Location ambiguous (local class vs. separate file) |
| Section 2 (Source-generator `value` params) | 100% | Confirmed correct |
| Section 3 (Interface `CancellationToken` params) | 100% | Confirmed correct |
| Section 4 (`GlobalUsings.cs`) | 100% | Confirmed correct |
| Section 5 (Confirmed-dead items) | 100% | All verified as dead code |
| Section 6 (Safe bulk actions) | 70% | "7 unused packages" claim needs full verification |
| Section 7 ("Do not remove" items) | Not verified | Assumed correct pending manual check |

**Overall accuracy: ~95%**

---

## Flagged Inaccuracies Requiring Clarification

### 1. `NullOpenVinoAvailabilityProvider` location ambiguity

**Issue:** The review says the audit marked `NullOpenVinoAvailabilityProvider` as "safe to remove," but there are TWO versions:
- A **local class** inside `OnnxExecutionSessionFactory.cs` (might be unused outside that file)
- A **separate file** `Inference/Runtime/Planning/NullOpenVinoAvailabilityProvider.cs` (definitely used)

**Recommendation:** Clarify which one the audit flagged. If it's the local class, it might actually be dead code.

---

### 2. "7 unused `PackageVersion` entries" unverified

**Issue:** The review claims 7 unused `PackageVersion` entries in `Directory.Packages.props` are safe to remove, but sample checks show at least 2 HAVE references (`AvaloniaUI.DiagnosticsSupport`, `Lucene.Net.Analysis.Common`).

**Recommendation:** Provide the list of 7 packages and VERIFY each one with:
```bash
grep -r "PACKAGE_NAME" **/*.csproj
```

**Actual unused packages found in my verification:**
- `JetBrains.Annotations` - Zero references in `.csproj` files

---

### 3. `ModelCacheDiagnostics` duplication claim unverified

**Issue:** The review claims `ModelCacheDiagnostics` logic is duplicated in `DiagnosticsCollector`, but this wasn't verified.

**Recommendation:** Verify that `DiagnosticsCollector` actually has a private method duplicating `ModelCacheDiagnostics.DetermineModelCacheEntry()`.

---

## Final Recommendations

1. **Accept the review recommendations** with the following caveats:
   - **Do NOT blindly remove 7 `PackageVersion` entries** - Verify each one first
   - **Clarify which `NullOpenVinoAvailabilityProvider` the audit flagged** (local class vs. separate file)
   - **Verify `ModelCacheDiagnostics` duplication claim** before removing

2. **Execute in this order:**
   - [ ] Run `dotnet format --no-restore` (safe, high-impact)
   - [ ] Remove `JetBrains.Annotations` from `Directory.Packages.props` (confirmed unused)
   - [ ] Delete 9 confirmed-dead files (Section 5)
   - [ ] Remove 4 dead members (Section 5)
   - [ ] Build + test

3. **Do NOT touch:**
   - Source-generator partial method `value` parameters
   - Interface `CancellationToken` parameters  
   - `NullOpenVinoAvailabilityProvider` (separate file version)
   - `GlobalUsings.cs` files (run `dotnet format` on them instead)

---

## Summary

The audit review document is **highly accurate** and provides **actionable recommendations**. The verification confirmed:
- ✅ ~80 false positives (source-generator params)
- ✅ ~10 misclassified interface params  
- ✅ ~10 confirmed-dead items (converters, properties, types)
- ✅ `GlobalUsings.cs` should NOT be blindly deleted
- ⚠️ "7 unused packages" claim needs verification
- ⚠️ `NullOpenVinoAvailabilityProvider` location needs clarification

**Next steps:** The user should execute the safe removals first (confirmed-dead files/members), verify the 7 package versions, and clarify the `NullOpenVinoAvailabilityProvider` ambiguity before proceeding.

# Trackdub Simplification & Modernization Audit

**Auditor:** Kiro (manual source inspection)  
**Date:** July 2026  
**Branch:** `main`  
**Target:** .NET 10 / C# 13 (`LangVersion=latest`, some projects `preview`)

---

## Methodology

This audit was conducted by direct source code inspection, pattern searching, and architectural analysis -- not from ReSharper XML output. Focus areas:

1. Async/await anti-patterns (deadlock risk, thread blocking)
2. Nullability fragility
3. C# modernization opportunities
4. Architecture smells and structural complexity

Findings are ranked by **risk** (could cause bugs/deadlocks) and **impact** (widespread pattern vs. isolated instance).

---

## 1. Sync-over-Async (RISK: deadlock / thread starvation)

These block threads waiting on async results. In UI or ASP.NET contexts, this risks deadlocks.

| Severity | File | Line | Pattern | Risk |
|----------|------|------|---------|------|
| **High** | `Application/Licensing/ExportTierGate.cs` | property getter | `InitializeAsync().GetAwaiter().GetResult()` | Deadlock if called from UI SynchronizationContext |
| **High** | `Inference.Onnx/QwenAssistant/QwenLocalAssistantEngine.cs` | `IsAvailable` property | `PlanAsync().GetAwaiter().GetResult()` | Property triggers async work synchronously; deadlock risk in UI |
| **Medium** | `Infrastructure/Persistence/Repositories/LocalModelCacheRecordLookup.cs` | sync interface impl | `LoadAsync().GetAwaiter().GetResult()` | Blocks on every model lookup; interface should be async |
| **Medium** | `Inference.Onnx/Kokoro/KokoroVoiceCatalog.cs` | `Load()` static method | `LoadAsync(...).ConfigureAwait(false).GetAwaiter().GetResult()` | ConfigureAwait mitigates deadlock but still blocks thread |
| **Low** | `Inference.Onnx/Kokoro/EspeakNgPhonemizer.cs` | after `WaitForExit` | `readTask.GetAwaiter().GetResult()` | Process already exited so task is completed; still, method could be fully async |
| **Low** | `Media.Playback/LibMpvWindowsBootstrap.cs` | bootstrap path | `.GetAwaiter().GetResult()` | One-time startup; ConfigureAwait(false) used |
| **Low** | `Sdk/HeadlessDubbingSessionFactory.cs` | factory init | `.GetAwaiter().GetResult()` | Headless CLI context, no SyncContext; acceptable |

**Recommended fix:** Make `ExportTierGate` and `QwenLocalAssistantEngine.IsAvailable` truly async (change property to `Task<bool>` method or use lazy async initialization). For `LocalModelCacheRecordLookup`, make the interface async.

---

## 2. Fire-and-Forget without Error Handling

Discarded tasks where exceptions vanish silently.

| File | Pattern | Risk |
|------|---------|------|
| `App.Avalonia/Services/ProjectLoadCoordinator.cs` | `_ = LoadProjectInternalAsync(...)` | Swallowed exceptions on project load |
| `App.Avalonia/Playback/AvaloniaPlaybackComposition.cs` | `_ = PrewarmAsync()` | Silent failure on playback native bootstrap |
| `App.Avalonia/ViewModels/AvaloniaMainWindowViewModel.*.cs` | Multiple `_ = SomeAsync()` in VM code | Standard Avalonia pattern, but needs `.ContinueWith(t => Log(t.Exception))` or equivalent |

**Note:** Fire-and-forget in Avalonia ViewModels is common (you can't await from a property change handler). The fix is adding `.ContinueWith(t => ..., TaskContinuationOptions.OnlyOnFaulted)` or using a centralized error handler.

---

## 3. Fragile Null-Forgiving Operator (!) Patterns

Places where `!` is used on fields/properties that are nullable due to partial initialization, creating hidden NRE risk if initialization order changes.

| Severity | File | Pattern | Risk |
|----------|------|---------|------|
| **High** | `ViewModels/AvaloniaMainWindowViewModel.cs` | `ExportMix!`, `_operationRunner!`, `settingsService!`, `projectSession!` | Nullable fields used with `!` assuming prior initialization; fragile if startup order changes |
| **Medium** | `Inference.Onnx/Qwen3Tts/Qwen3TtsEngine.cs` | `request.VoiceCloneReference!.ReferenceTranscript!` | Second `!` is 37 lines after the null guard; fragile if code reorders |
| **Medium** | `Media.Playback/LibMpvCompositedPlaybackBackend.cs` | `mpv_create!()`, `mpv_initialize!()`, etc. | Native function pointers declared nullable, invoked with `!` at every call site instead of guarding once at load |

**Recommended fix:** For AvaloniaMainWindowViewModel, use `[MemberNotNull]` attributes or extract required services into a non-nullable initialization record. For LibMpv, validate all function pointers at load time and throw, then store as non-nullable.

---

## 4. Redundant Defensive Checks

`ArgumentNullException.ThrowIfNull` on parameters that are already non-nullable (with `Nullable=enable`):

| File | Parameter |
|------|-----------|
| `Application/Projects/SegmentStageRunProvenanceStore.cs` | `IReadOnlyList<int> allSegmentIndices` |
| `Application/Projects/ProjectMediaIngestService.cs` | various non-nullable params |
| `Application/Transcripts/SubtitleExportService.cs` | various non-nullable params |

**Note:** This is a style debate. With `Nullable=enable`, the compiler prevents null at call sites. ThrowIfNull adds runtime defense for callers that suppress warnings. Low priority but adds noise.

---

## 5. `System.Threading.Lock` Migration (20+ instances)

.NET 9+ introduced `System.Threading.Lock` which is more efficient than `lock(object)`. The project targets net10.0.

**Top candidates:**

| File | Field | Usage |
|------|-------|-------|
| `App.Avalonia/Playback/AvaloniaVideoFramePresenter.cs:15` | `private readonly object sync` | lock(sync) on lines 47/57/80/109/125 |
| `Inference.Onnx/OnnxExecutionSessionFactory.cs:35` | `private static readonly object _initLock` | Static lock for EP initialization |
| `App.Avalonia/Services/VoiceCloneConsentCoordinator.cs:7` | `private readonly object gate` | Consent coordination |
| `Application/Licensing/ExportTierGate.cs:14` | `private readonly object InitGate` | License init gate |
| `Application/Transcripts/TranscriptWorkspace.cs:29` | `private readonly object disposalSync` | Disposal guard |
| `Inference.Onnx/Pool/OnnxSessionPool.cs` | lock fields | Session pool management |
| `Media.Playback/LibMpvCompositedPlaybackBackend.cs` | multiple lock objects | Playback state |

**Fix:** Replace `private readonly object x = new();` with `private readonly Lock x = new();` -- drop-in replacement, better JIT optimization.

---

## 6. Non-Sealed Classes (Design Issue)

Classes without virtual members or inheritance intent should be `sealed` per project conventions. Unsealed classes:
- Prevent devirtualization optimizations
- For IDisposable: create GC finalization overhead

| File | Class | Issue |
|------|-------|-------|
| `Application/Services/ProjectService.cs` | `ProjectService` | No virtual members, not inherited |
| `Application/Services/SessionService.cs` | `SessionService` | No virtual members |
| `Application/Services/TranslationService.cs` | `TranslationService` | No virtual members |
| `Application/Services/VoiceAssignmentService.cs` | `VoiceAssignmentService` | No virtual members |
| `App.Avalonia/ViewModels/Dev/DevLogViewModel.cs` | `DevLogViewModel : IDisposable` | **IDisposable without sealed** -- GC perf concern |
| `Inference.Onnx/Qwen3Tts/Pipeline/QwenTtsOptions.cs` | `QwenTtsOptions` | Mutable options bag, no inheritance |
| `Inference.Onnx/Qwen3Tts/Pipeline/TextToSpeechOptions.cs` | `TextToSpeechOptions` | Same |

**Fix:** Add `sealed` keyword. For `DevLogViewModel`, seal or add a destructor suppression.

---

## 7. Duplicated Constants

| Location A | Location B | What's duplicated |
|------------|------------|-------------------|
| `Inference.Onnx/Qwen3Asr/Qwen3AsrPromptTokens.cs` | `Inference.Onnx/Qwen3Tts/` token constants | Token IDs: EndOfText=151643, ImStart=151644, ImEnd=151645, AudioStart=151669, AudioEnd=151670 |

**Fix:** Extract to shared `Qwen3SharedTokens` constant class in `Inference.Onnx/Qwen3/` or a shared location both ASR and TTS reference.

---

## 8. Architecture Smells

### 8a. God Class: AvaloniaMainWindowViewModel

- **Main file:** 2386 lines
- **Partial files:** 16+ (`PipelineUi`, `Panels`, `SegmentEdit`, `History`, `PreviewMix`, `ProjectLoad`, `ProjectImport`, `GlossaryHighlights`, `PipelineStageExecutionHost`, `SpeakerVoice`, `SegmentPipeline`, `SidecarCommands`, `StudioSettings`, `Subtitles`, `Timeline`, `Waveform`)
- **Injected dependencies:** 15+
- **Service locator usage:** Resolves 5+ services from `IServiceScopeFactory` at runtime

The partial file split helps readability but doesn't address the coupling. This VM is the coordinator for nearly all UI state.

**Recommendation:** Not actionable as a simplification (it's a known architectural debt). Document as a candidate for extraction into focused coordinators if/when the left-panel pipeline UX redesign lands.

---

### 8b. Service Locator in ViewModels

Multiple ViewModel partial files resolve services at runtime via `IServiceScopeFactory`:

```csharp
using var scope = _scopeFactory.CreateScope();
var service = scope.ServiceProvider.GetRequiredService<IModelInventoryService>();
```

Found in: `PipelineUi`, `GlossaryHighlights`, `SegmentEdit`, `SpeakerVoice`, `StudioSettings`

**Why it exists:** Lazy resolution avoids circular dependencies and keeps ctor injection manageable on the god-class VM.

**Recommendation:** Accept as pragmatic trade-off until VM extraction reduces dependency count. Not a simplification candidate today.

---

### 8c. Namespace Density: `Trackdub.Application.Transcripts`

~80+ files in a single namespace. Types range from stage handlers to DTOs to workflows to services.

**Recommendation:** Split into sub-namespaces when next refactoring this area:
- `Transcripts.Stages/` (stage handlers)
- `Transcripts.Models/` (DTOs, contracts)
- `Transcripts.Workflows/` (orchestration)

---

### 8d. Duplicated Stage Handler Boilerplate

Every stage handler follows an identical pattern:
1. Check prerequisites
2. Call `StageRunHelper.StartKnownStageRun()`
3. Execute core logic
4. Write artifacts
5. Complete stage run

`StageRunHelper.StartKnownStageRun` has an exhaustive switch on stage names that must grow with every new stage. All branches call the same factory method.

**Recommendation:** Consider a base class or pipeline middleware pattern to eliminate the ceremony. The switch statement can be replaced with a dictionary or attribute-based registration.

---

## 9. Byte Array Allocation Opportunities (Span/stackalloc)

| File | Current | Opportunity |
|------|---------|-------------|
| `Inference.Onnx/Qwen3Tts/Models/NpyReader.cs:91` | `byte[] headerBytes = new byte[10]` | `Span<byte> headerBytes = stackalloc byte[10]` (small fixed-size header read) |
| `Media/Waveforms/WavePcm16.cs` | Various `byte[]` for WAV header parsing | stackalloc for 44-byte WAV headers |
| `Media/Extraction/Pcm16WaveClipExtractor.cs` | Buffer allocations for audio chunks | `ArrayPool<byte>.Shared` for large buffers |

**Impact:** Low-medium. Only matters in hot paths (batch TTS, waveform generation).

---

## 10. Quick Wins (High confidence, mechanical)

These can be applied in bulk with minimal review:

| Category | Count | Fix |
|----------|-------|-----|
| `lock(object)` to `System.Threading.Lock` | ~20 | Drop-in replacement |
| Add `sealed` to leaf classes | ~10-15 in Application/Inference | Add keyword |
| Redundant `!` after confirmed non-null | ~15-20 in App.Avalonia | Remove operator |
| `new List<T>()` to `[]` where target-typed | ~40+ | Collection expression |
| `Enumerable.Empty<T>()` to `Array.Empty<T>()` or `[]` | ~5 | Swap |

---

## Summary

| Category | Count | Risk | Effort |
|----------|-------|------|--------|
| Sync-over-async (potential deadlocks) | 7 | **High** | Medium (interface changes needed) |
| Fire-and-forget without error handling | ~10 | Medium | Low (add continuation) |
| Fragile `!` patterns | ~20 | Medium | Low-Medium |
| `System.Threading.Lock` migration | ~20 | None (improvement) | Low |
| Non-sealed classes | ~10 | Low (perf) | Trivial |
| Duplicated Qwen3 tokens | 1 instance | Low | Trivial |
| God class / service locator | 1 class | Architectural | High (not a simplification task) |
| Namespace density | 1 namespace | Organizational | Medium |
| Span/stackalloc opportunities | ~5 | None (perf) | Low |
| Collection expression modernization | ~40 | None (style) | Low |

---

## Recommended Priority

### P0 -- Fix Now (Risk of bugs)
1. `ExportTierGate` sync-over-async in property getter -- potential deadlock
2. `QwenLocalAssistantEngine.IsAvailable` sync-over-async -- same

### P1 -- Fix Soon (Quality)
3. `System.Threading.Lock` migration (20 instances) -- free perf
4. Seal leaf classes in Application (4-5 classes)
5. Seal `DevLogViewModel` (IDisposable concern)
6. Extract shared Qwen3 token constants

### P2 -- Fix When Touching (Cleanup)
7. Remove redundant `!` operators where nullable field is always initialized
8. LibMpv function pointer validation at load time (remove per-call `!`)
9. `LocalModelCacheRecordLookup` interface to async
10. Collection expressions, Span opportunities, redundant ThrowIfNull

### P3 -- Track (Architectural)
11. AvaloniaMainWindowViewModel decomposition (when pipeline UX redesign lands)
12. `Application.Transcripts` namespace split
13. Stage handler boilerplate reduction

# Simplification Audit Review: Accuracy & False Positives

**Source:** `Trackdub-simplification-audit-codex-refactor.md` (ReSharper `jb inspectcode` on `codex/refactor`)  
**Reviewed by:** Kiro, verified against `main` source code (July 2026)

---

## Executive Summary

The audit is structurally sound -- rules, counts, and representative samples are real ReSharper findings. However, several categories have significant false-positive rates or produce dangerous recommendations when applied mechanically:

- **55 items invalid** (`ReplaceWithFieldKeyword` -- wrong LangVersion for most projects)
- **~15 items dangerous** (`UseNameOfInsteadOfToString` -- breaks enum persistence)
- **~30 items impractical** (`AsyncVoidEventHandlerMethod` -- XAML event handler requirement)
- **~30 items test false positives** (`AccessToDisposedClosure` -- safe within test scope)
- **~25 items wrong for config classes** (`MemberCanBePrivate` on IOptions properties)

**Estimated accuracy:** ~95% of items are real findings, but ~225 of 4298 should not be applied as-is.

---

## 1. DANGEROUS: `UseNameOfInsteadOfToString` (21 items)

The audit suggests replacing `tier.ToString()` and `JobStatus.Running.ToString()` with `nameof`. This is **wrong and dangerous**.

**Why it breaks:**

```csharp
// CURRENT (correct):
TenantTier tier = TenantTier.Free;
await _publisher.PublishSubscriptionUpdatedAsync(tenantId, tier.ToString(), ...);
// Produces: "Free"

// AUDIT SUGGESTION (broken):
await _publisher.PublishSubscriptionUpdatedAsync(tenantId, nameof(tier), ...);
// Produces: "tier" (the variable name, NOT the enum value)
```

**Specific dangerous instances:**
- `BillingService.cs:76` -- `TenantTier.Free.ToString()` used in subscription event publishing
- `BillingService.cs:227` -- same pattern
- `BillingService.cs:296` -- same pattern
- `ConcurrencyGuard.cs:60` -- `JobStatus.Running.ToString()` used in DynamoDB filter expressions (persisted query value)
- `DynamoDbJobQueue.cs:68` -- `JobStatus.Queued.ToString()` in DynamoDB expressions

For the DynamoDB cases, even `nameof(JobStatus.Running)` (which does produce `"Running"`) is fragile: renaming the enum member silently changes the query string and breaks against existing persisted data.

**Verdict:** The entire bucket needs manual review. Only cases where code does something like `throw new ArgumentException(nameof(param))` or logging are safe candidates. At minimum **5 of the 5 samples shown are false positives**.

**Action:** Do not bulk-apply. Review each instance individually. The enum-variable and DynamoDB cases must stay as `.ToString()`.

---

## 2. INVALID: `ReplaceWithFieldKeyword` (55 items)

The C# `field` keyword for semi-auto properties requires `LangVersion=preview`.

**Actual LangVersion configuration:**
- `Directory.Build.props`: `LangVersion=latest` (solution-wide default)
- Only 5 projects override to `preview`: `WebhookDelivery`, `Api.Billing.Tests`, `Api.Tests`, `Sdk.Tests`, `Worker.Tests`

The 55 flagged items are overwhelmingly in `App.Avalonia` and `Application` which both inherit `latest`. These suggestions **will not compile**.

**Verdict:** Remove all `ReplaceWithFieldKeyword` items for projects using `LangVersion=latest`. Only valid for the 5 projects explicitly on `preview` (and those have approximately zero instances in this list).

**Action:** Drop entire category from the actionable list unless LangVersion is upgraded solution-wide to `preview`.

---

## 3. IMPRACTICAL: `AsyncVoidEventHandlerMethod` (36 items)

Most flagged methods are XAML event handlers that Avalonia **requires** to be `async void`:

```csharp
// Avalonia XAML: <Button Click="NewFromMediaButton_Click" />
// Handler MUST be async void -- cannot return Task
private async void NewFromMediaButton_Click(object? sender, RoutedEventArgs e) { ... }
```

**Confirmed XAML-bound handlers:**
- `CenterPanelView.axaml.cs:217` -- `NewFromMediaButton_Click`
- `CenterPanelView.axaml.cs:221` -- `OpenProjectButton_Click`
- `GlossaryPanelView.axaml.cs:15` -- event handler
- `CrashReportWindow.axaml.cs:121` -- button handler

Avalonia's event system uses standard .NET event delegates (`EventHandler<RoutedEventArgs>`) which return `void`. You cannot change the return type to `Task` without breaking the event subscription.

**Verdict:** ~30 of 36 are false positives for Avalonia XAML event handlers. Only code-subscribed events (e.g., `observable.Subscribe(async () => ...)`) or manually wired delegates might be fixable.

**Action:** Investigate only non-XAML instances. For XAML handlers, the correct mitigation is wrapping the body in try/catch (which most already do), not changing the signature.

---

## 4. FALSE POSITIVES: `AccessToDisposedClosure` (41 items, mostly tests)

In test code, this pattern is safe:

```csharp
[Fact]
public async Task Some_test()
{
    using var sut = new SystemUnderTest();
    var result = await sut.DoSomethingAsync(); // flagged: "sut disposed in outer scope"
    Assert.True(result);
} // sut disposed here, AFTER all assertions
```

ReSharper flags it because a lambda/async continuation *could* outlive the `using`, but in linear xUnit test methods this never happens.

**Confirmed test false positives:**
- `ConcurrencyGuardTests.cs:180`
- `TaskLauncherDuplicateDetectionPropertyTests.cs:81, 88, 179, 186`

**Verdict:** ~30 of 41 are test code false positives. Any production code instances (e.g., closures passed to background tasks) should be investigated individually.

**Action:** Ignore test instances. Review the ~11 production code instances case-by-case.

---

## 5. PARTIALLY WRONG: `MemberCanBePrivate.Global` (519 items) + `AutoPropertyCanBeMadeGetOnly.Global` (160 items)

These flag properties on **`IOptions<T>` configuration classes** which ASP.NET binds from `appsettings.json`:

```csharp
// CognitoOptions.cs -- bound via builder.Services.Configure<CognitoOptions>(config)
public sealed class CognitoOptions
{
    public string Region { get; init; } = "";      // flagged: "can be private"
    public string UserPoolId { get; init; } = "";  // flagged: "can be private"
    public string ClientId { get; init; } = "";    // flagged: "can be private"
}
```

Making these `private` breaks configuration binding. The properties use `init` (so `AutoPropertyCanBeMadeGetOnly` is technically already satisfied), but `MemberCanBePrivate` is wrong.

**Affected classes (non-exhaustive):**
- `CognitoOptions` (3 properties)
- `MigrationOptions`
- `WebhookOptions`
- `TaskLauncherOptions` (3 properties)
- `ApiKeyAuthenticationOptions`

**Verdict:** Subtract ~20-30 items from `MemberCanBePrivate.Global` for IOptions/config classes. These must remain `public` (or at minimum `internal`) for config binding to work.

**Action:** Skip all `MemberCanBePrivate` findings on classes that implement options patterns or are registered with `Configure<T>()`.

---

## 6. LOW VALUE: `ForCanBeConvertedToForeach` (12 items)

Spot-checked `UiHelpers.cs` (4 of 12). All loops access elements via `list[i]` on `IReadOnlyList<T>`:

```csharp
for (int i = 0; i < segments.Count; i++)
{
    var seg = segments[i];  // index access
    if (seg.StartSeconds <= position) ...
}
```

While technically convertible (IReadOnlyList implements IEnumerable), index-based access:
- Avoids enumerator allocation (relevant in hot UI render paths)
- Is idiomatic for ordered boundary searches with early break

**Verdict:** Not wrong, but low-value refactoring with potential micro-perf regression in UI code. The existing style is intentional.

**Action:** Low priority. Skip for hot-path UI code.

---

## 7. VALID BUT NEEDS JUDGMENT: `ConvertToPrimaryConstructor` (79 items)

Legitimate for simple DI constructors (field assignment only). The `DynamoDbSubscriptionStore` example is a clean candidate. However, classes with:
- Constructor body logic (validation, initialization)
- Multiple constructors
- `this(...)` chaining

...cannot be cleanly converted.

**Verdict:** Valid for ~60 of 79 (simple DI constructors). Remaining ~19 need manual review.

**Action:** Apply in bulk for obvious DI constructors. Skip classes with constructor body logic.

---

## CONFIRMED CORRECT: Safe to apply mechanically

| Rule | Count | Confidence | Notes |
|------|-------|------------|-------|
| `RedundantSuppressNullableWarningExpression` | 82 | Very high | Remove redundant `!` -- safe, no behavior change |
| `ConditionalAccessQualifierIsNonNullableAccordingToAPIContract` | 17 | Very high | Remove unnecessary `?.` |
| `NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract` | 19 | Very high | Remove unnecessary `??` |
| `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` | 31 | High | Simplify dead branches |
| `UseAwaitUsing` | 64 | Very high | `await using` for IAsyncDisposable |
| `RedundantCast` | 31 | Very high | Remove unnecessary casts |
| `RedundantNameQualifier` | 282 | Very high | `dotnet format` handles this |
| `RedundantExplicitArrayCreation` | 17 | Very high | Use `[]` or collection expression |
| `RedundantLambdaParameterType` | 40 | Very high | Remove explicit lambda types |
| `RedundantArgumentDefaultValue` | 42 | High | Remove args matching defaults |
| `RedundantAttributeUsageProperty` | 12 | Very high | Remove redundant attribute props |
| `RedundantSwitchExpressionArms` | 11 | Very high | Remove unreachable arms |
| `RedundantTypeArgumentsOfMethod` | 27 | Very high | Remove inferable type args |
| `MergeIntoPattern` | 202 | High | Style improvement |
| `UseCollectionExpression` | 43 | High | Valid for net10.0 |
| `ChangeFieldTypeToSystemThreadingLock` | 23 | High | Valid for net10.0 (>= net9.0) |
| `PossibleMultipleEnumeration` | 10 | Very high | Real perf issue |
| `InconsistentNaming` | 1604 | High | `dotnet format` + `.editorconfig` |
| `FieldCanBeMadeReadOnly.Local` | 15 | Very high | Safe mechanical fix |
| `SimplifyLinqExpressionUseAll` | 18 | High | Readability improvement |
| `UseObjectOrCollectionInitializer` | 29 | High | Style improvement |
| `ArrangeObjectCreationWhenTypeEvident` | 46 | High | Target-typed `new()` |
| `ConvertClosureToMethodGroup` | 16 | High | Style improvement |
| `RedundantAnonymousTypePropertyName` | 8 | Very high | Safe removal |
| `RedundantAssignment` | 29 | High | Remove dead assignments |
| `VariableCanBeNotNullable` | 23 | High | Tighten nullability |
| `ReturnTypeCanBeNotNullable` | 15 | High | Tighten nullability |
| `CanSimplifyDictionaryTryGetValueWithGetValueOrDefault` | 17 | High | Valid simplification |
| `PropertyCanBeMadeInitOnly.Global` | 91 | Medium | Valid but check IOptions classes |
| `PropertyCanBeMadeInitOnly.Local` | 15 | High | Safe for local/test types |
| `AutoPropertyCanBeMadeGetOnly.Local` | 20 | High | Safe for local types |
| `ParameterHidesMember` | 15 | High | Valid rename candidates |

---

## VALID BUT CASE-BY-CASE

| Rule | Count | Notes |
|------|-------|-------|
| `MethodHasAsyncOverload` | 97 | Valid but verify: some sync calls are intentional (thread-safety, hot paths, sync-over-async avoidance) |
| `AsyncMethodWithoutAwait` | 10 | Valid -- but some may return `Task.FromResult` intentionally for interface compliance |
| `ConvertToAutoProperty` | 15 | Check for side effects in getter/setter before converting |
| `MethodSupportsCancellation` | 14 | Valid but adding CT propagation can be a larger refactor |
| `ParameterOnlyUsedForPreconditionCheck.Local` | 41 | Often intentional guard clauses -- not actually dead parameters |
| `UsingStatementResourceInitialization` | 11 | Valid but verify exception safety of the specific pattern |

---

## Corrected Priority Counts

| Priority | Original | False Positives | Net Actionable |
|----------|----------|----------------|----------------|
| High | 538 | ~100 | ~438 |
| Medium | 1004 | ~75 | ~929 |
| Low | 2756 | ~50 | ~2706 |
| **Total** | **4298** | **~225** | **~4073** |

---

## Recommended Execution Order

### Wave 1: Mechanical (no judgment needed)

```powershell
# Redundant qualifiers, naming, format
dotnet format --no-restore
```

Then apply via ReSharper/Rider cleanup profile:
- `RedundantSuppressNullableWarningExpression` (82)
- `RedundantCast` (31)
- `RedundantExplicitArrayCreation` (17)
- `RedundantLambdaParameterType` (40)
- `RedundantArgumentDefaultValue` (42)
- `RedundantTypeArgumentsOfMethod` (27)
- `RedundantSwitchExpressionArms` (11)
- `RedundantAnonymousTypePropertyName` (8)
- `FieldCanBeMadeReadOnly.Local` (15)

### Wave 2: High-value, safe with minimal judgment

- `UseAwaitUsing` (64)
- `ChangeFieldTypeToSystemThreadingLock` (23)
- `PossibleMultipleEnumeration` (10)
- `ConditionalAccessQualifierIsNonNullableAccordingToAPIContract` (17)
- `NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract` (19)
- `ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract` (31)
- `UseCollectionExpression` (43)
- `MergeIntoPattern` (202)
- `SimplifyLinqExpressionUseAll` (18)

### Wave 3: Requires per-instance judgment

- `ConvertToPrimaryConstructor` (79) -- skip classes with constructor body logic
- `MethodHasAsyncOverload` (97) -- verify each callsite
- `MemberCanBePrivate.Global` (519) -- skip IOptions classes
- `AutoPropertyCanBeMadeGetOnly.Global` (160) -- skip config/serialization classes
- `PropertyCanBeMadeInitOnly.Global` (91) -- skip IOptions classes

### DO NOT APPLY

- `ReplaceWithFieldKeyword` (55) -- wrong LangVersion
- `UseNameOfInsteadOfToString` (21) -- dangerous for enums, review individually
- `AsyncVoidEventHandlerMethod` (36) -- mostly XAML handlers, cannot change
- `AccessToDisposedClosure` (41) -- mostly test false positives

### After each wave

```powershell
dotnet build Trackdub.sln -m:1 -p:Platform=x64 -warnaserror
dotnet test Trackdub.sln -m:1 -p:Platform=x64
```
