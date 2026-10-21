# Trackdub SnapSoft GPU POC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task after approval. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing Trackdub GPU CLI usable through a durable asynchronous job interface without duplicating pipeline logic.

**Architecture:** Provisionally extend `trackdubllc/api.trackdub` (Cloudflare Worker/D1/R2), using durable submission/outbox, scheduled dispatch/reconciliation, and AWS Batch EC2 jobs wrapping the unchanged application pipeline through its CLI host. A small supervisor stages input, starts CLI safely, publishes artifacts and reports execution evidence. SnapSoft chooses Batch versus direct ECS; an AWS-only control plane is an explicit alternate requiring the host/storage tasks to be revised before implementation.

**Tech Stack:** Existing .NET 10 CLI/SDK/Application/Composition, Hono/TypeScript/Better Auth, D1/Drizzle, R2, proposed AWS Batch on GPU EC2, digest-pinned derived worker image.

**Spec:** [Source assessment and proposed contracts](../specs/2026-10-21-snapsoft-gpu-poc-design.md).

**Status:** PROPOSED. No task below has been implemented. Michael explicitly requested approval before implementation. No infrastructure provisioning, external configuration change, chargeable execution or CI rerun is authorized by this plan alone. Publishing these planning documents does not approve implementation.

## Global constraints

- Trackdub is the canonical project name.
- Keep all pipeline execution logic in shared application/SDK services; HTTP and cloud adapter hosts only orchestrate jobs.
- Keep the existing one-shot `server` CLI image/entrypoint usable; create a derived adapter image only where required.
- No parsing human-readable CLI logs as a durable protocol.
- One active GPU job per POC host, one reserved GPU per task, serial seed/warm.
- One D1 authority for business jobs in the reuse option; no second DynamoDB/SQLite cloud job store.
- Durable acceptance means committed job plus dispatch intent; no in-memory-only accepted work.
- No success on abnormal native/process exit, missing strict result or missing required uploaded artifacts.
- Preserve unrelated work; re-inspect branch/status before implementation and isolate feature changes as appropriate.
- Cross-platform .NET 10; no desktop/Windows dependency added to the Linux cloud path.
- API/cloud adapter/SnapSoft allocations remain proposed until confirmed.
- Exclude formal benchmarking/cost analysis; retain functional validation.

## Review focus

- Lost Batch SubmitJob response must not start two pipelines: exercise duplicate scheduler executions against one conditional start claim (Task 3/4).
- Result emitted before native teardown crash must never yield success: test child prints/writes success then exits 139 (Task 2/4).
- Cancel racing publication must leave one consistent terminal state: test conditional generation/version updates and terminal cancellation only after cessation (Task 1/4).
- Upload completes before final callback/API restart: reconcile existing verified objects without rerunning pipeline (Task 3/4).
- Shared model/SQLite/process globals are unsafe assumptions: validate serial GPU admission, separate project DBs, closed SQLite backup and exact mounted cache root (Task 2/5).

## Files and interfaces

Core root is `/workspace/Trackdub`. The API repo was read remotely, not checked out or modified; paths prefixed `api.trackdub/` are repository-qualified proposed paths, not existing local absolute paths.

Proposed interfaces (names are design decisions, not current source):

```text
TypeScript:
  JobService.submit(ownerId, key, CreateJobRequest): Promise<SubmissionResult>
  JobService.getOwned(ownerId, jobId): Promise<JobDto>
  JobService.requestCancel(ownerId, jobId): Promise<CancelResult>
  JobService.listArtifacts(ownerId, jobId): Promise<ArtifactList>
  JobStore.acceptWithOutbox(ownerId, key, requestHash, request): Promise<SubmissionResult>
  JobStore.claimExecution(jobId, dispatchId, executionId): Promise<ExecutionClaim>
  JobStore.recordProgress(claim, event): Promise<void>
  JobStore.recordCandidate(claim, outcome): Promise<void>
  JobStore.finalize(expectedVersion, terminalEvidence): Promise<boolean>
  JobScheduler.submit(DispatchRequest): Promise<SchedulerExecution>
  JobScheduler.describe(executionIds): Promise<SchedulerExecution[]>
  JobScheduler.stop(executionId, reason): Promise<void>

C#:
  WorkerRunOutcome: schemaVersion, status, DubbingRunResult?, errorCode,
                    message, artifact inventory, warnings
  CliMachineResultWriter.WriteAsync(WorkerRunOutcome, string resultPath,
                                   CancellationToken): Task
  IJobControlClient.ClaimAsync(JobExecutionIdentity, CancellationToken): Task<ExecutionClaim>
  IJobControlClient.HeartbeatAsync(ExecutionClaim, CancellationToken): Task<LeaseStatus>
  IJobControlClient.ReportProgressAsync(ExecutionClaim, PipelineProgressEvent,
                                      CancellationToken): Task
  IJobControlClient.ReportCandidateAsync(ExecutionClaim, AttemptOutcome,
                                       CancellationToken): Task
  CliProcessRunner.RunAsync(CliExecutionSpec, CancellationToken): Task<CliExitEvidence>
  JobAdapter.RunAsync(JobExecutionIdentity, CancellationToken): Task<int>
```

The TypeScript DTO schema and C# process envelopes need checked-in JSON examples/schema and compatibility tests. Do not introduce code generation/tooling unless hand-maintained fixtures prove insufficient. `JobDto`, `CreateJobRequest` and state semantics are defined in the spec. `ExecutionClaim` carries job/attempt/execution/dispatch IDs, fencing generation, expiry and private transfer capabilities; never expose it to clients.

## Task 0: Confirm boundaries and freeze the functional fixture

**Files:** update the accompanying design/plan after decisions; proposed new `docker/poc-server-preset.json`, `docs/operations/snapsoft-poc-acceptance.md`, fixture metadata under `tests/fixtures/cloud-poc/` (check in only media with redistribution permission).

**Consumes:** inspected core/API source and spec decision list.
**Produces:** approved service/storage/ownership choices, preset version, media hash and required-stage/artifact expectations.

- [ ] Confirm existing API reuse versus AWS-only requirement, Batch/ECS, target GPU/AMI/driver, registry, funded scope and execution owner with Michael/SnapSoft.
- [ ] Inspect current branches/working trees and rebase evidence to actual implementation commits; preserve any existing user changes.
- [ ] Define `poc-server-v1`: pinned model aliases/variants, provider behavior, EN→ES voice, export format and required stages. Prove seed selection and runtime selection agree; qualify optional EN→ZH separately.
- [ ] Record one licensed non-silent fixture checksum, expected nonempty ASR/translation/TTS output, required export, acceptable optional skips and proposed operational limits. Never accept silent pass-through as full dubbing proof.

Task 0 is a decision/fixture gate, not permission to provision. If AWS-only is required, revise Tasks 1/3 around a CPU ASP.NET host + DynamoDB/S3 and an explicit migration/compatibility strategy for the existing API; do not implement both control planes.

## Task 1: Durable API acceptance and compatible job contract

**Modify (API repo):** `src/routes/dubs.ts`, `src/db/schema.ts`, `src/lib/dubs.ts`, `src/lib/errors.ts`, `src/lib/capabilities.ts`, `src/index.ts`, `src/types/env.ts`, `test/dubs.test.ts`, `test/dubs-helpers.test.ts`, CORS tests.

**Create (API repo):** `src/jobs/contracts.ts`, `src/jobs/job-service.ts`, `src/jobs/job-store.ts`, `src/routes/jobs.ts`, `test/jobs-contract.test.ts`, `test/job-store.test.ts`; generated Drizzle migration under `drizzle/` for immutable uploads, idempotency, job version/phase, outbox and attempts/artifacts.

**Consumes:** approved schema v1/preset from Task 0.
**Produces:** the four proposed `/jobs` routes and existing `/api/dubs` compatibility over the same `JobService`/D1 records. A 202 response commits submission plus outbox atomically.

- [ ] Write D1/workerd tests asserting transaction rollback leaves neither accepted job nor orphan dispatch intent; API/store error returns 503, not 202. Race same-owner/key submissions and assert one job/outbox; changed request under same key gets 409; other owner cannot observe it.
- [ ] Implement DTO validation, immutable upload metadata/checksum binding, supported preset/media limits, ownership filtering, request hash and unique idempotency constraint with atomic D1 batch. Use existing safe error envelope. Keep current upload cap honest.
- [ ] Test cancelled-before-dispatch creates no execution; running cancel returns cancelRequested, not terminal Cancelled. Completion/cancel CAS race yields one winning terminal outcome. Test repeated cancellation and illegal terminal transitions.
- [ ] Implement versioned routes and legacy DTO/status mappings without copying handlers. Extend CORS for Idempotency-Key. Preserve existing auth/authorization; keep capability disabled pending a real processor.
- [ ] Test empty artifact list before publication, foreign-owner 404, signed link refresh and legacy create/get/delete/download compatibility. Assert no process-local accepted queue.
- [ ] Verify with `bun install --frozen-lockfile`, `bun run typecheck`, and `bun test` in the API checkout. Commit only reviewed task files after tests pass; do not deploy or run remote migrations.

## Task 2: Strict CLI machine result and progress boundary

**Modify (core):** `src/Trackdub.Cli/Commands/RunPipelineCommandOptions.cs`, `RunPipelineCommandExecutor.cs`, `src/Trackdub.Cli/Handlers/RunPipelineHandler.cs`, `src/Trackdub.Cli/CliProgressReporter.cs`, `CliProgressRunner.cs`, `Program.cs` as needed for explicit host signals, `src/Trackdub.Cli/Trackdub.Cli.csproj` if required.

**Create:** `src/Trackdub.Contracts/Jobs/WorkerRunOutcome.cs`, `src/Trackdub.Cli/CliMachineResultWriter.cs`, `src/Trackdub.Cli/CliProgressFileSink.cs`; tests `tests/Trackdub.Sdk.Tests/CliMachineResultTests.cs`, `CliProgressFileSinkTests.cs`, `CliSignalCancellationTests.cs`.

**Reuse:** `TrackdubDubbingEngine.ExecuteAsync`, `DubbingRunResult`, `PipelineProgressEvent`, `ProjectLock.Acquire`. Keep `RunManifestWriter` informational; no broad rewrite needed.

**Consumes:** fixed preset and schema v1.
**Produces:** `run pipeline --result-file <path> --progress-file <path>` with existing stdout/stderr preserved; exit 130 for pipeline cancellation; strict atomic result and clean dedicated JSONL progress.

- [ ] Write fake-engine tests for succeeded, required-stage failure, preflight failure, optional benign skip/degradation and cancellation. Assert result schema/status, typed result, artifact inventory and exit mapping; partial success only succeeds when goal/required export exists.
- [ ] Implement option plumbing and a small host-level sink; keep `RunPipelineHandler.IsGoalAchieved` semantics explicit and avoid moving pipeline logic. Where the preset needs a per-stage provider map, map the reviewed preset into existing `DubbingSessionOptions.ExecutionProviderPreferences`, not a new planner. Do not allow arbitrary client file/preset paths.
- [ ] Test unwritable result path returns nonzero; file is atomic; interrupted write is never mistaken for a final result. Write failure/cancel envelope with a bounded non-cancelled cleanup token. Early parse/argument errors remain exit 1 and may have no result, which the adapter must handle.
- [ ] Test JSONL is valid and ordered under concurrent reports, excludes diagnostic text and preserves nullable percentage/run/sequence. Reuse existing progress event data rather than reconstructing from logs.
- [ ] Add a Linux subprocess fixture asserting SIGTERM reaches cancellation, process returns 130, and dependent child processes are reaped; adapter kill remains the fallback for uninterruptible native code. Preserve Windows Ctrl+C behavior.
- [ ] Run focused SDK tests, including existing `RunPipelineHandlerGoalTests`, `CancellationPropagationTests`, `RunManifestWriterTests` and `ProjectLockTests`, against portable `net10.0`. Run portable CLI Release build with warnings as errors. Commit after meaningful tests pass; no GPU needed for this task.

## Task 3: Durable dispatcher, execution claim and reconciliation

**Modify (API repo):** `src/index.ts` to export fetch plus scheduled handler, `src/types/env.ts`, `src/config/env.ts`, `wrangler.jsonc` draft binding/cron configuration, migrations/schema from Task 1.

**Create (API repo):** `src/jobs/dispatcher.ts`, `src/jobs/reconciler.ts`, `src/jobs/scheduler.ts`, `src/jobs/batch-scheduler.ts` (or only `ecs-scheduler.ts` if chosen), `src/routes/internal-jobs.ts`, `src/jobs/service-auth.ts`, `src/jobs/transfer-capabilities.ts`, tests in `test/job-dispatcher.test.ts`, `job-reconciler.test.ts`, `internal-jobs.test.ts`.

**Consumes:** Task 1 store/contracts and authenticated internal service identity design.
**Produces:** bounded scheduled dispatch, `JobScheduler`, conditional start claim and fenced heartbeat/candidate publication, queue/deadline/cancel reconciliation.

- [ ] Write fake-scheduler tests for API restart after acceptance, outage before submit, lost submit response, lease recovery and repeated dispatch. With Batch, simulate two execution IDs for one dispatch and assert exactly one start claim succeeds. Job names must not be treated as unique keys. With ECS, assert stable `clientToken` and fixed task definition revision.
- [ ] Implement D1 outbox lease/CAS and fixed scheduler request generation. Propose direct SigV4 AWS calls from existing Worker using least-privilege credentials stored through its secret mechanism; SnapSoft must approve cross-cloud identity/rotation. Do not put secrets in source/task arguments/logs. If policy requires an AWS-side bridge, revise scope before adding it.
- [ ] Implement authenticated internal claim/heartbeat/progress/candidate endpoints; claims bind execution identity and generation. Reject wrong/expired identity, stale attempts, replayed progress sequences and unauthenticated/browser callers. Use job-scoped transfer URLs, not bucket-wide credentials in the client.
- [ ] Test leases expire without authorizing replacement until previous execution is confirmed stopped. Test unauthorized attempt cannot mint URLs for another job/key. Configure heartbeat 15 seconds, lease 90 seconds unless Task 0 changes them.
- [ ] Test successful candidate and uploaded objects still require scheduler exit 0; exit 139 overrides a success candidate. Unknown/missing result fails. Verify required artifacts exist with size/hash and generation before final CAS. Partial diagnostic uploads cannot imply success.
- [ ] Test queued/starting/running deadline mapping, Batch cancel-versus-terminate race, out-of-order/repeated scheduler events, completion/cancellation race and missing callback. Keep one retry owner: Batch attempts=1; at most two logical attempts for the allowed infrastructure cases, no retry on deterministic pipeline/native errors.
- [ ] Verify API typecheck and workerd tests. Do not apply remote migration, configure cron/secrets or invoke AWS. Commit code/config drafts only after tests pass.

## Task 4: One-shot supervisor and artifact publication

**Create (core):** `src/Trackdub.CloudJobAdapter/Trackdub.CloudJobAdapter.csproj`, `Program.cs`, `JobAdapter.cs`, `JobExecutionIdentity.cs`, `JobControlClient.cs`, `CliProcessRunner.cs`, `ArtifactPublisher.cs`, `TransferClient.cs`, `ProcessShutdown.cs`; `tests/Trackdub.CloudJobAdapter.Tests/` covering supervision and protocol; `Dockerfile.cloud-worker`.

**Modify:** `Trackdub.slnx` and/or a portable solution filter; `.dockerignore` only if needed for the new build; `.github/workflows/container-image.yml` to build/verify the adapter profile after approved CLI machine changes. Keep existing server/rtx outputs.

**Consumes:** Task 2 strict process protocol and Task 3 claim/control endpoints.
**Produces:** one supervisor that wraps the existing CLI; fixed argv, isolated filesystem, hash-verified transfers, candidate evidence and correct process exit. Scheduler success is provisional until durable job finalization.

- [ ] Write subprocess-fixture tests: normal success; exit 0 without result; failed result; printed/written success then exit 139; hang; exit 137 with/without durable cancel intent. None may parse human output for outcome.
- [ ] Implement claim-before-work; private per-attempt input/project/tmp paths; `ProcessStartInfo.ArgumentList` with fixed CLI path and translated approved preset only. Test quotes/semicolon/command-substitution strings remain data or are rejected; no shell is involved. Reject symlink/relative-path escape in artifacts.
- [ ] Verify input checksum/size and ffprobe metadata. Enforce execution timeout; heartbeat while downloading/running/publishing. If control lease cannot be renewed before expiry, stop the CLI and decline publication. Do not grant a replacement while the old execution might run.
- [ ] Tail dedicated progress JSONL, throttle snapshots, retain run IDs/sequences, and report through authenticated attempt control. Stage percent stays nullable; never average stage values into a fabricated whole-job percent.
- [ ] Upload allowlisted export/subtitles/strict result to immutable attempt keys. Test interruption after each upload and loss of final callback; retry transfers/callback without restarting CLI. Reconciler recovers from candidate/result objects. Close project connections and use safe SQLite backup if project snapshot is selected in Task 0.
- [ ] Implement SIGTERM forwarding with 20-second internal grace inside 30-second stop window, then process-tree termination/reaping. Test cooperative and hung child; terminal state derives from intent/deadline/scheduler cessation, not signal number alone.
- [ ] Create a derived adapter image from the approved digest, copy only supervisor publish output, retain non-root ownership and CLI dependencies, override its entrypoint explicitly. Test an approved no-GPU fake workload in CI; no weights/GPU acceptance is claimed.
- [ ] Run adapter/portable CLI builds and focused tests. Verify both original image profiles still package as before; do not broaden testing without a new failure or concern. Commit reviewed files. Do not push a billable build or run target GPU here.

## Task 5: Scheduler/IaC drafts and honest operator runbook

**Modify (core):** `docs/operations/cloud-gpu-container.md`, `.env.example`, `docker-compose.aws.yml` and `docker-compose.yml` only for verified effective options, `scripts/docker/smoke-gpu-matrix.sh`; corresponding existing script tests if present.

**Create:** `docs/operations/snapsoft-poc-acceptance.md`, `scripts/docker/verify-cloud-job-artifacts.sh` (or equivalent native validation tool); proposed SnapSoft-owned IaC path `infra/snapsoft-poc/` only after repo ownership/tooling is agreed.

**Correct:** stale references in `docs/development/TROUBLESHOOTING.md`, aggregate `development.md`, readiness spec/ADR and their aggregates; annotate historical split plans. Do not rewrite unrelated documentation or modify aggregate outputs without identifying their generation convention.

**Consumes:** approved service/host/image/preset and code contracts from Tasks 0–4.
**Produces:** reviewable resource/task definitions, one canonical server runbook and acceptance harness; no resources applied.

- [ ] Draft GPU reservation, fixed image/definition, task execution versus application role, log retention, queue/deadline/retry settings, host capacity ceiling, EBS mount/UID and seed/warm bootstrap barrier. Prevent scheduling until preparation succeeds. Specify whether capacity stays warm or scales down; container exit alone does not stop instance billing.
- [ ] Document registry pull credentials/ECR mirroring and exact digest mapping; CF-to-AWS scheduler identity and adapter-to-API/transfer identity; outbound network access and secret rotation. SnapSoft confirms the integration fits funding/SOW.
- [ ] Keep prepared model/engine storage separate from job workspaces. Define container-replacement durability and optional same-AZ host reattachment; avoid a fleet-shared WAL DB or concurrent model-index mutation. Do not claim host-loss persistence from an EBS bind mount alone.
- [ ] Fix smoke verdict aggregation to retain inference verdict and exit health separately; process exit 139 must fail acceptance even when JSON says passed. Test zero/failed/no-JSON/crash-after-success cases with canned outputs, without GPU execution. A matrix containing failed required cells must not return overall acceptance success.
- [ ] Replace stale API `.env`/Swagger/compose claims with CLI controls and separate API links; spell out actual `/data/models` settings/index/log roots and project-local SQLite. Align seed/warm docs with AWS server override and pinned digest.
- [ ] Validate shell syntax and dry-run rendering; lint IaC locally using the selected tool without provider apply. Review permissions and mounts against actual code. Commit only agreed docs/templates. Deployment remains a later, explicit review step.

## Task 6: Approved-environment functional acceptance and handoff

**Files:** acceptance checklist/evidence manifest under `docs/operations/snapsoft-poc-acceptance.md`; runtime evidence goes to the agreed access-controlled artifact location, with links/hashes and no credentials.

**Consumes:** accepted Tasks 0–5, separately authorized AWS environment/resources, known fixture and pinned releases.
**Produces:** reproducible pass/fail record for every checkbox in spec section 7 and signed-off ownership/runbook handoff.

- [ ] Run the agreed target-GPU seed/provider/admission/cache verification and E2E fixture; capture actual process exits plus structured evidence. Confirm image pull on that host.
- [ ] Exercise durable API restart, duplicate submit/lost scheduler response, failure, queued/running cancel, abnormal native exit, timeout, container replacement, retrievable artifacts and foreign-owner denial.
- [ ] Record all source/image/definition/model/host identities; verify downloaded checksums and nonempty translated speech, not merely successful export. Document any stage degradation or limitation as a failed/unmet criterion unless explicitly allowed beforehand.
- [ ] Confirm no formal benchmark/cost-analysis deliverable was added. Record diagnostic durations only as context; do not turn them into throughput/cost claims.
- [ ] Hand off task/job definitions, secret/registry setup procedure, canonical runbook, acceptance evidence, known limitations and proposed-versus-confirmed responsibilities. Do not mark target readiness complete until this evidence exists.

## Review status and approval gate

Self-review checked source/design separation, all eight requested deliverables, API compatibility, acceptance atomicity, duplicate execution, cancellation races, retries/deadlines, artifact finalization and infrastructure ownership. The unresolved choices are explicitly listed in the spec and Task 0; they are not hidden implementation placeholders.

The plan is ready for Michael's review. The approval gate comes directly from the pasted request: **“Do not begin implementation until the plan is approved.”** No code, infrastructure or external service changes should be made before that approval. Confirm the API/storage boundary and SnapSoft responsibility/service choices before dependent tasks begin.
