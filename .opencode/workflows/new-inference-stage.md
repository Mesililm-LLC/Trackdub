# New Inference Stage

Takes a new dubbing/inference stage from intake to proven readiness in the Trackdub public core. This is the multi-agent path: the orchestrator drives, `pipeline-inference` writes layers in dependency order, `core-diagnostics` owns every failure, and `validation-gate` renders the final verdict. It exists because the most expensive defect class here is a **faked readiness claim** — a provider registered, a manifest entry present, a stage compiling, and no proof the stage downloaded a model, ran inference, and produced output. Every stage below ends in an observable fact, not an intention.

## When to Use This Workflow

Use when:
- Adding a new pipeline stage (diarization, lip-sync, translation variant, enhancement, anything with a `StageName`).
- Adding a new inference provider or execution-provider binding to an existing stage.
- Re-enabling or re-pinning a stage that currently reports skipped/disabled.
- Changing stage ordering, prerequisites, or artifact-resume behavior.

Do NOT use for:
- Bug fix with no stage-graph change → `.opencode/context/processes/pr-lifecycle.md` via `/ready`.
- Performance work with no structural change → `benchmark-regression-triage.md`.
- Bump of an existing ONNX/DNNL/Substrate pin → `.opencode/context/processes/submodule-pin-bump.md` via `/pin`.
- UI-only work touching `src/Trackdub.Sdk` or `src/Trackdub.Cli` surfaces with no pipeline effect.

## Inputs

| Input | Required | Description | Default |
|---|---|---|---|
| Stage identity | yes | Canonical kebab-case name; becomes `StageNames.*` constant value | none |
| Stage intent | yes | One sentence: what artifact it consumes and what it emits | none |
| Needs new provider | yes | `true` = new inference provider; `false` = orchestration over existing providers | required |
| Model requirement | if provider | Model id/alias as it appears in the bundled manifest | none |
| License posture | if provider | Must be commercial; unknown license is unsafe | none |
| Prerequisite stages | yes | Upstream stage names that must have succeeded | none |
| Fixture for proof | yes | Media fixture used for the execution proof | `$env:LOCALAPPDATA\Trackdub\benchmark-fixtures\baseline-v1\short.mp4` |

## Preconditions

- `Trackdub.slnx` builds clean before any edit: `dotnet build Trackdub.slnx -m:1`.
- The AGENTS.md dependency diagram and every `src/**.csproj` `ProjectReference` are in sync (enforced by `DependencyGraphTests.AgentsMdDiagramMatchesEveryCsprojProjectReference`).
- `Trackdub.Domain` has zero project references (`DependencyGraphTests.DomainHasNoProjectReferences`).
- `Trackdub.Contracts` references `Trackdub.Domain` and nothing else (ADR-0011, `DependencyGraphTests.ContractsReferencesOnlyDomain`).
- If a provider is added, the model has a commercial license and an entry in `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`.
- No new end-user runtime dependency (Python, Conda, Docker, CUDA Toolkit) is introduced — forbidden by AGENTS.md Model Governance.

## Execution Stages

### Stage 1 - Intake and Scope Confirmation

- **Goal**: Fix the stage contract before any code exists.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/domain/terminology.md`, `.opencode/context/domain/inference-stack.md`, `.opencode/context/processes/adding-pipeline-stage.md`
- **Actions**:
  1. State the stage name, consumed artifact, produced artifact, and prerequisite stage list.
  2. Decide `needs new provider`. If yes, enumerate the provider type and the execution providers it may bind to.
  3. Confirm model license is commercial. Unknown license ⇒ **abort**, do not speculate.
  4. Confirm the model belongs in `bundled-models.manifest.json` and state its checksum source.
  5. Do not use Linear or require issue searches/status/TS IDs by default. Track work in Git, create persisted plans only when requested, and update an external tracker only on explicit user request.
- **Exit criteria**: A written stage contract exists; provider decision is binary; license is confirmed commercial.
- **Failure handling**: Ambiguous contract, missing model, or unknown license ⇒ stop and hand to human. Do not proceed to implementation.

### Stage 2 - Contracts Layer

- **Goal**: Add the stage's external shape without leaking inference upward.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/domain/architecture.md`, `.opencode/context/standards/architecture-rules.md`
- **Actions**:
  1. Add the stage to the Contracts surface only where a consumer genuinely needs it.
  2. `Trackdub.Contracts` may reference `Trackdub.Domain` and nothing else — no new `ProjectReference`.
  3. Never expose provider types, session handles, or tensor shapes above Contracts.
- **Exit criteria**: `dotnet build Trackdub.slnx -m:1` succeeds; Contracts reference set unchanged.
- **Failure handling**: Any upward leak → `core-diagnostics`.

### Stage 3 - Domain Layer

- **Goal**: Add the canonical stage name constant and any pure value types.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/domain/architecture.md`, `.opencode/context/standards/coding-standards.md`
- **Actions**:
  1. Add `public const string <Name> = "<kebab-case>";` to `src/Trackdub.Domain/StageRuns/StageNames.cs`.
  2. Add the same string to `KnownStageNameValues` in `tests/Trackdub.Architecture.Tests/StageNameConsistencyTests.cs`. **This second step is mandatory** — `KnownStageNameValues_covers_all_StageNames_constants` fails without it, and skipping it silently disables inline-literal detection for the new stage.
  3. Domain models are immutable `record`s, file-scoped namespace, `sealed` where extension is not intended.
  4. Domain stays pure: no I/O, no inference, zero project references.
- **Exit criteria**: `dotnet test tests/Trackdub.Domain.Tests --no-restore -m:1` green; `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1` green.
- **Failure handling**: Domain test failure or dependency-graph breach → `core-diagnostics`.

### Stage 4 - Application Layer (Stage Implementation)

- **Goal**: Implement the stage against the real pipeline contract.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/domain/architecture.md`, `.opencode/context/standards/coding-standards.md`, `src/Trackdub.Application/Pipeline/README.md`
- **Actions**:
  1. Implement `ITranscriptGenerationStage` (`src/Trackdub.Application/Transcripts/Pipeline/ITranscriptGenerationStage.cs`): `string StageName { get; }` plus `ExecuteAsync(TranscriptGenerationContext, CancellationToken, IProgress<PipelineProgressEvent>?)`.
  2. Return `StageNames.<Name>` from `StageName`. Never inline the string literal at a `StageRunRecord.Start(...)` call site — `StageRunRecord_Start_never_receives_inline_string_literal` enforces this.
  3. Keep model-specific tensor manipulation **out** of `src/Trackdub.Application/Pipeline`; that directory is orchestration only.
  4. Honor the four mandatory pipeline paths: success, disabled/skipped, missing-prerequisite, failure.
  5. Preserve original artifacts on skip or failure and record an explicit skip/failure reason.
  6. Register the stage on the builder via `ITranscriptPipelineBuilder.AddStage(ITranscriptGenerationStage, StageOptions?)`.
  7. Model-specific work requires fakes from `tests/Trackdub.TestDoubles/` (shared via `<Compile Include>`), not real models.
  8. Use `Path.Join`, not `Path.Combine` — see `BannedSymbols.txt`. Currently a build **warning**, not an error; do not introduce new occurrences.
- **Exit criteria**: `dotnet build Trackdub.slnx -m:1` clean; `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1` green.
- **Failure handling**: Build/test failure, stage-registration failure, or artifact-destruction on skip → `core-diagnostics`.

### Stage 5 - Inference / Inference.Onnx (only if a new provider)

- **Goal**: Add provider capability without letting inference leak upward.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/domain/inference-stack.md`, `.opencode/context/processes/submodule-pin-bump.md`
- **Actions**:
  1. Add the provider in `src/Trackdub.Inference` (references: Contracts, Domain) or `src/Trackdub.Inference.Onnx` (references: Inference, Contracts, Domain).
  2. `src/Trackdub.Domain` gains nothing. `src/Trackdub.Application` gains nothing.
  3. If Windows ONNX Runtime packages are involved, they must use the WinML catalog provider — enforced by `DependencyGraphTests.WindowsOnnxRuntimePackagesUseWinMlCatalogProvider` over `Directory.Packages.props`, `Trackdub.Inference.Onnx.csproj`, and `Trackdub.Composition.csproj`.
  4. Add/refresh the manifest entry in `bundled-models.manifest.json` with commercial license metadata.
  5. Do not hand-resolve a `packages.lock.json` merge conflict. Take one side (`git checkout --ours -- <path>` or `--theirs`), then `dotnet restore Trackdub.slnx --force-evaluate -m:1`.
- **Exit criteria**: `dotnet test tests/Trackdub.Inference.Tests --no-restore -m:1` and `dotnet test tests/Trackdub.Inference.Onnx.Tests --no-restore -m:1` green.
- **Failure handling**: Provider fails to register or load, manifest invalid, package conflict → `core-diagnostics`.

### Stage 6 - Composition: DI Registration and Snapshot Enablement

- **Goal**: Wire the stage so it can actually be constructed and selected at runtime.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/domain/architecture.md`, `.opencode/context/processes/adding-pipeline-stage.md`
- **Actions**:
  1. Register services inside `src/Trackdub.Composition/CompositionRoot.cs` (`AddTrackdub(this IServiceCollection)`), keeping `Composition`'s reference set at Application, Inference, Inference.Onnx, Infrastructure, Licensing, Media, Media.Playback.
  2. Add the stage to the runtime stage enumeration so readiness and snapshot selection can include it.
  3. If the stage is warmable or resumable, integrate with `IStageReadinessOrchestrator` / `IStageWarmupCoordinator` and the artifact-resume evaluator; do not fork the readiness path.
  4. Keep `src/Trackdub.Sdk` → Application, Composition, Licensing and `src/Trackdub.Cli` → Sdk intact.
- **Exit criteria**: `dotnet test tests/Trackdub.Composition.Tests --no-restore -m:1` green; `dotnet run --project src/Trackdub.Cli --framework net10.0 -- --help` runs clean and lists the stage.
- **Failure handling**: DI resolution failure, duplicate registration, or CLI not listing the stage → `core-diagnostics`.

### Stage 7 - Tests

- **Goal**: Prove the stage's contract across all required paths.
- **Actor**: subagent `pipeline-inference`; review by `trackdub-orchestrator`
- **Context to load**: `.opencode/context/standards/coding-standards.md`, `.opencode/context/standards/validation-gates.md`
- **Actions**:
  1. Add unit tests in the matching project for success, disabled/skipped, missing-prerequisite, and failure.
  2. Keep the Architecture suite green — it is the structural contract: `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1`.
  3. Keep Domain tests pure and I/O-free; use `tests/Trackdub.TestDoubles` for Application fakes.
  4. Confirm the two-step `StageNames` sync in Stage 3 actually holds by re-running Architecture tests after all edits.
- **Exit criteria**: Matching test project green **and** Architecture tests green.
- **Failure handling**: Any red test → `core-diagnostics`. Do not relax an assertion to make a test pass.

### Stage 8 - Execution Proof (no readiness claim without proof)

- **Goal**: Demonstrate the stage actually ran and produced output.
- **Actor**: subagent `pipeline-inference`
- **Context to load**: `.opencode/context/processes/adding-pipeline-stage.md`, `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. Run the stage end to end on the fixture. Verify the exact command surface with `dotnet run --project src/Trackdub.Benchmarks.DevHost -f net10.0 -- --help`.
  2. Drive the pipeline so the stage is genuinely selected, not merely registered.
  3. Capture the produced artifact and its path. Confirm the artifact is a real, non-empty output — not a placeholder.
  4. Capture `ActualProvider` and `ActualModel` from the benchmark report (`Stages[].ActualProvider`, `Stages[].ActualModel`); report what actually executed, not what was configured.
  5. Record explicit skip or failure reasons if the stage did not run.
- **Exit criteria**: Stage ran, produced an artifact, and the report names the real provider and model.
- **Failure handling**: Stage skipped, produced nothing, or silently no-op'd → treat as **not ready**; route to `core-diagnostics`.

### Stage 9 - Validation Gate

- **Goal**: Issue the single authoritative readiness verdict.
- **Actor**: subagent `validation-gate`
- **Context to load**: `.opencode/context/standards/validation-gates.md`, `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. Run the full CI-equivalent gate:
     ```bash
     dotnet restore Trackdub.slnx -m:1
     dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
     dotnet test Trackdub.slnx --configuration Release --no-build -m:1
     ```
  2. Re-check Architecture invariants against the final tree.
  3. Confirm each of the four readiness claims independently: **registered ≠ downloaded ≠ ran ≠ produced output**. Registration, manifest presence, or file existence alone proves nothing about running.
  4. Emit PASS or FAIL with the failing command and its exact output.
- **Exit criteria**: Release build with warnings-as-errors passes; full Release test suite passes; all four readiness claims are individually evidenced.
- **Failure handling**: Any FAIL → `core-diagnostics`, then re-enter this stage. Never round a FAIL to PASS on assertion weakening.

### Stage 10 - Readiness Audit

- **Goal**: Confirm nothing claims readiness beyond what was proven.
- **Actor**: subagent `validation-gate`
- **Context to load**: `.opencode/context/standards/validation-gates.md`, `.opencode/context/domain/inference-stack.md`
- **Actions**:
  1. Confirm readiness evaluation uses runtime validation. `IPipelineReadinessService.EvaluateAsync` accepts `validateRuntime`; `true` performs a real runtime smoke test (ONNX session creation and inference), `false` stops at file-existence checks and reports Ready only.
  2. A badge or status derived from `validateRuntime: false` is **Ready**, never **Verified**. Report the distinction honestly.
  3. Confirm skipped stages state a reason rather than appearing green.
- **Exit criteria**: No surface in the repo reports "ready/verified" for the new stage beyond proven capability.
- **Failure handling**: Overstated readiness → `core-diagnostics`; correct the claim at its source.

### Stage 11 - Documentation and Handoff

- **Goal**: Leave the repo self-describing.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/navigation.md`, `.opencode/ARCHITECTURE.md`
- **Actions**:
  1. Update `.opencode/ARCHITECTURE.md` and `.opencode/navigation.md` if stage topology changed.
  2. If the dependency graph changed, update the AGENTS.md diagram — Architecture tests compare it against every csproj reference in both directions.
  3. Do not use Linear. **Never mark Done without proof**; the proof is the Stage 9 gate output plus Stage 8 artifacts.
- **Exit criteria**: Docs consistent with code.
- **Failure handling**: Docs/code drift → `core-diagnostics`.

## Decision Points

| Condition | Decision |
|---|---|
| Stage needs a new inference provider | Run Stage 5; else skip to Stage 6 |
| Model license unknown or non-commercial | Abort the workflow; hand to human |
| Stage executes model-specific tensor work | Move logic out of `Application/Pipeline`; keep orchestration there |
| Stage is warmable or artifact-resumable | Integrate `IStageReadinessOrchestrator` / `IStageWarmupCoordinator` |
| Readiness needs a runtime smoke test | `validateRuntime: true`; `false` only for UI badge refresh |
| `packages.lock.json` conflicts | Take one side, then `dotnet restore Trackdub.slnx --force-evaluate -m:1` |
| Validation gate FAILs | Enter `core-diagnostics`; do not advance |
| Proof shows stage ran but produced no artifact | Not ready — return to Stage 8, then Stage 4 |
| Change must not ship (abandoned) | Execute rollback path below |

## Gates

- **G0 — Intake gate** (end Stage 1): contract written, provider decision binary, license commercial.
- **G1 — Structural gate** (end Stages 3, 5): Domain has zero references; Contracts references only Domain; AGENTS.md diagram matches every csproj.
- **G2 — Build/test gate** (end Stage 7): matching test project green, Architecture tests green.
- **G3 — Readiness gate** (Stage 9): Release `-warnaserror` build + full Release test suite.
- **G4 — Claim gate** (end Stage 10): no surface overstates readiness; `Registered ≠ Downloaded ≠ Ran ≠ Produced`.
- **Rollback / abort path**: At any gate failure, halt. Preserve original artifacts from the failed stage. Revert the branch rather than leaving a half-wired stage registered — a partially registered stage that fails at runtime produces silent skips, which is worse than an absent one. If already merged, open a revert PR and disable the stage until it lands.

## Failure Modes

| Symptom | Likely cause | Recovery |
|---|---|---|
| Architecture test: csproj not listed in AGENTS.md diagram | New `ProjectReference` without diagram edit | Add the project and its deps to the AGENTS.md diagram |
| Architecture test: diagram lists project not under `src/` | Stale diagram entry after project removal | Remove the entry; the test checks both directions |
| `KnownStageNameValues_covers_all_StageNames_constants` fails | `StageNames` constant added, test list not updated | Add the value to `KnownStageNameValues` |
| Inline-literal test fails at a new call site | Literal passed to `StageRunRecord.Start` | Use the `StageNames.*` constant |
| `DomainHasNoProjectReferences` fails | Inference pulled into Domain | Remove the reference; move logic to `Inference` or later |
| `ContractsReferencesOnlyDomain` fails | Contracts reached sideways | Remove the extra reference; invert with an interface |
| Windows ONNX package test fails | Wrong catalog provider in `Directory.Packages.props` | Use the WinML catalog provider in the three named files |
| Stage resolves but never executes | Not added via `AddStage`, or absent from stage enumeration | Register in `CompositionRoot.AddTrackdub` and the enumeration |
| Stage runs, produces no artifact | Success path returns early; artifact not persisted | Fix the emit path; preserve failure reasons; re-prove Stage 8 |
| Status shows Ready, nothing runs | Readiness evaluated with `validateRuntime: false` | Use `validateRuntime: true` for any run-readiness claim |
| `dotnet restore` lock conflicts | Hand-merged `packages.lock.json` | Take one side, `--force-evaluate` |
| Full CI passes but stage still unproven | Correctness ≠ readiness | Re-enter Stage 8/9; G3 is not proof of execution |

## Evidence to Collect

Record under `.opencode/context/templates/evidence-report.md`:
- Stage contract and prerequisite list.
- Exact commands run, verbatim, with pass/fail.
- Release `-warnaserror` build and full Release test results.
- Architecture test output for the structural gates.
- Execution proof: artifact path, non-empty size, `actualProvider`, `actualModel` from the benchmark report.
- Readiness verdict per claim: registered / downloaded / ran / produced output — each individually marked.
- Files changed, by project, with the dependency-edge justification for each new reference.

## Completion Checklist

- [ ] Stage contract confirmed; `needs new provider` decided.
- [ ] Commercial license confirmed; manifest entry present with checksum source.
- [ ] `StageNames` constant added **and** mirrored into `KnownStageNameValues`.
- [ ] `StageName` returns the constant; no inline literal at any `StageRunRecord.Start` call site.
- [ ] Domain has zero project references; Contracts references only Domain.
- [ ] No inference types leaked above Inference/Contracts.
- [ ] AGENTS.md diagram matches every csproj reference, both directions.
- [ ] Success, disabled/skipped, missing-prerequisite, and failure paths all tested.
- [ ] Original artifacts preserved on skip/failure with explicit reasons.
- [ ] Stage registered in `CompositionRoot.AddTrackdub` and the stage enumeration.
- [ ] CLI lists the stage: `dotnet run --project src/Trackdub.Cli --framework net10.0 -- --help`.
- [ ] Execution proof captured with real artifact, `actualProvider`, `actualModel`.
- [ ] `validateRuntime` semantics respected — no Ready presented as Verified; Release build passes `-warnaserror` with the full Release test suite green.
- [ ] No new `Path.Combine` in changed lines; docs updated; no external issue tracker touched unless explicitly requested by the user; no Done claim without proof.
