# Trackdub SnapSoft GPU POC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task after approval. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the existing Trackdub GPU CLI usable through a durable asynchronous job interface without duplicating pipeline logic.

**Architecture:** AWS-only API/storage is required by Michael. Add a lightweight ASP.NET Core CPU API with shared cloud orchestration services, DynamoDB durable submission/outbox/attempt state and S3 versioned media/results. Background dispatch/reconciliation runs in the API host; AWS Batch EC2 jobs use a small supervisor around the existing CLI. SnapSoft aligns Batch versus direct ECS; no Cloudflare job API, D1 or R2 runtime dependency is permitted.

**Tech Stack:** .NET 10 ASP.NET Core and existing CLI/SDK/Application/Composition, AWS SDK for .NET, DynamoDB, S3, proposed Cognito JWT authentication, CPU ECS/Fargate API and Batch on GPU EC2, digest-pinned worker images. Exact hosting/auth provisioning remains proposed.

**Spec:** [Source assessment and proposed contracts](../specs/2026-10-21-snapsoft-gpu-poc-design.md).

**Status:** AWS-only API/storage requirement CONFIRMED; implementation PROPOSED. No task below has been implemented. Michael explicitly requested approval before implementation. No infrastructure provisioning, external configuration change, chargeable execution or CI rerun is authorized by this plan alone. Publishing these planning documents does not approve implementation.

## Global constraints

- Trackdub is the canonical project name.
- Keep all pipeline execution logic in shared application/SDK services; HTTP and cloud adapter hosts only orchestrate jobs.
- Keep the existing one-shot `server` CLI image/entrypoint usable; create a derived adapter image only where required.
- No parsing human-readable CLI logs as a durable protocol.
- One active GPU job per POC host, one reserved GPU per task, serial seed/warm.
- DynamoDB is the sole business-job authority; S3 is the sole media/result/artifact object store. No Cloudflare API/D1/R2 runtime dependency. Local SQLite remains project state only.
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
- Upload completes before final outcome write/API restart: reconcile existing verified objects without rerunning pipeline (Task 3/4).
- Shared model/SQLite/process globals are unsafe assumptions: validate serial GPU admission, separate project DBs, closed SQLite backup and exact mounted cache root (Task 2/5).

## Files and interfaces

Core root is `/workspace/Trackdub`. Add proposed projects here, subject to confirming repository ownership before implementation: `Trackdub.Api` (CPU HTTP host), `Trackdub.Cloud` (job contracts/orchestration), `Trackdub.Cloud.Aws` (AWS adapters), and `Trackdub.CloudJobAdapter` (CLI supervisor). The historical private `api.trackdub` repository was inspected remotely; it supplies compatibility evidence only. Do not move or rewrite its auth/activation services as an incidental part of this POC.

Dependency direction: `Api → Cloud, Cloud.Aws`; `Cloud.Aws → Cloud, Contracts`; `Cloud → Contracts`; `CloudJobAdapter → Cloud, Cloud.Aws, Contracts`. Only the existing CLI process loads SDK/Composition/inference. API and cloud orchestration projects must not pull GPU/native packages through an SDK reference. Update dependency documentation explicitly when adding projects.

Proposed C# interfaces (not current source):

```text
IJobService.SubmitAsync(ownerId, key, CreateJobRequest, CancellationToken): Task<SubmissionResult>
IJobService.GetOwnedAsync(ownerId, jobId, CancellationToken): Task<JobDto>
IJobService.RequestCancelAsync(ownerId, jobId, CancellationToken): Task<CancelResult>
IJobService.ListArtifactsAsync(ownerId, jobId, CancellationToken): Task<ArtifactList>
IJobStore.AcceptWithOutboxAsync(ownerId, key, requestHash, request, CancellationToken): Task<SubmissionResult>
IJobStore.ClaimExecutionAsync(JobExecutionIdentity, CancellationToken): Task<ExecutionClaim>
IJobStore.RecordProgressAsync(ExecutionClaim, PipelineProgressEvent, CancellationToken): Task
IJobStore.RecordCandidateAsync(ExecutionClaim, AttemptOutcome, CancellationToken): Task
IJobStore.FinalizeAsync(expectedVersion, TerminalEvidence, CancellationToken): Task<bool>
IJobScheduler.SubmitAsync(DispatchRequest, CancellationToken): Task<SchedulerExecution>
IJobScheduler.DescribeAsync(executionIds, CancellationToken): Task<IReadOnlyList<SchedulerExecution>>
IJobScheduler.StopAsync(executionId, reason, CancellationToken): Task
IUploadService.CreateAsync(ownerId, UploadRequest, CancellationToken): Task<UploadReservation>
IUploadService.CompleteAsync(ownerId, uploadId, CancellationToken): Task<UploadReference>
WorkerRunOutcome: schemaVersion, status, DubbingRunResult?, errorCode,
                  message, artifact inventory, warnings
CliMachineResultWriter.WriteAsync(WorkerRunOutcome, string resultPath, CancellationToken): Task
IJobControlStore.ClaimAsync(JobExecutionIdentity, CancellationToken): Task<ExecutionClaim>
IJobControlStore.HeartbeatAsync(ExecutionClaim, CancellationToken): Task<LeaseStatus>
IJobControlStore.ReportProgressAsync(ExecutionClaim, PipelineProgressEvent, CancellationToken): Task
IJobControlStore.ReportCandidateAsync(ExecutionClaim, AttemptOutcome, CancellationToken): Task
CliProcessRunner.RunAsync(CliExecutionSpec, CancellationToken): Task<CliExitEvidence>
JobAdapter.RunAsync(JobExecutionIdentity, CancellationToken): Task<int>
```

Implement spec JSON DTOs as C# records with checked-in schema/example compatibility fixtures; the TypeScript-shaped illustration in the spec does not imply a TypeScript API. `ExecutionClaim` binds job/attempt/execution/dispatch IDs, fencing generation, expiry and approved S3 versioned references. `IJobControlStore` delegates to the same conditional mutation logic as `IJobStore` using the worker task role, not a public HTTP callback endpoint. Keep full run results in S3 and bounded summaries in DynamoDB.

## Task 0: Confirm boundaries and freeze the functional fixture

**Files:** update the accompanying design/plan after decisions; proposed new `docker/poc-server-preset.json`, `docs/operations/snapsoft-poc-acceptance.md`, fixture metadata under `tests/fixtures/cloud-poc/` (check in only media with redistribution permission).

**Consumes:** inspected core/API source and spec decision list.
**Produces:** approved service/storage/ownership choices, preset version, media hash and required-stage/artifact expectations.

- [ ] Record the confirmed AWS-only boundary; align CPU API hosting, Cognito JWT/PKCE flows, portal compatibility/cutover, Batch/ECS, target GPU/AMI/driver, registry, funded scope and execution owner with Michael/SnapSoft.
- [ ] Inspect current branches/working trees and rebase evidence to actual implementation commits; preserve any existing user changes.
- [ ] Define `poc-server-v1`: pinned model aliases/variants, provider behavior, EN→ES voice, export format and required stages. Prove seed selection and runtime selection agree; qualify optional EN→ZH separately.
- [ ] Record one licensed non-silent fixture checksum, expected nonempty ASR/translation/TTS output, required export, acceptable optional skips and proposed operational limits. Never accept silent pass-through as full dubbing proof.

Task 0 is a decision/fixture gate, not permission to provision. AWS-only is settled. Confirm whether any real legacy jobs/uploads exist before proposing migration; fresh POC jobs use AWS only. Define a cutover that disables legacy job intake and never creates two authorities; no automatic legacy-data migration or Cloudflare deletion is authorized.

## Task 1: AWS CPU API, durable acceptance and S3 upload contract

**Create:** `src/Trackdub.Api/Trackdub.Api.csproj`, `Program.cs`, `Endpoints/JobsEndpoints.cs`, `Endpoints/UploadsEndpoints.cs`, `Endpoints/LegacyDubsEndpoints.cs` (only if portal compatibility is required), `Authentication/JobAuthorization.cs`; `src/Trackdub.Cloud/Trackdub.Cloud.csproj`, `Jobs/JobContracts.cs`, `Jobs/JobService.cs`, `Jobs/IJobStore.cs`, `Uploads/IUploadService.cs`; `src/Trackdub.Cloud.Aws/Trackdub.Cloud.Aws.csproj`, `Jobs/DynamoDbJobStore.cs`, `Uploads/S3UploadService.cs`, `Storage/S3ArtifactStore.cs`; `Dockerfile.api`; `tests/Trackdub.Api.Tests/`, `tests/Trackdub.Cloud.Tests/` and `tests/Trackdub.Cloud.Aws.Tests/`, each with its project file.

**Modify:** `Trackdub.slnx`, `Directory.Packages.props` and project lockfiles using normal restore for approved AWS SDK/JWT/test packages; dependency architecture documentation. Use a CPU ASP.NET runtime image, not the GPU image.

**Consumes:** spec schema v1, confirmed AWS-only boundary and approved preset.
**Produces:** four `/jobs` endpoints, upload reserve/complete endpoints, JWT/owner authorization and atomic DynamoDB acceptance. Optional legacy routes map to the same records/service.

- [ ] Write store tests asserting a failed TransactWriteItems leaves no partial accepted job/outbox/idempotency mapping; errors return 503, not 202. Race same-owner/key submissions and assert one job; changed request hash yields 409. Use conditional deterministic idempotency item keys; do not rely only on short-lived transaction request tokens or TTL.
- [ ] Implement normalized request hash, job/idempotency/outbox transaction and strongly consistent duplicate lookup. Index pending work for discovery; conditional base-table writes control authority. Keep full result payloads out of DynamoDB items and in S3.
- [ ] Test reserve/upload/complete flow with immutable S3 version/checksum references, incomplete upload, oversized object, wrong owner, replaced version and expired presigned URL. Validate completion server-side; a signed URL alone cannot enforce all acceptance limits. Keep bucket private/versioned/encrypted in IaC drafts. Worker re-verifies staged bytes/hash.
- [ ] Test queued cancellation versus dispatch and running cancelRequested versus completion with version/generation CAS; no false terminal Cancelled before cessation. Repeated cancel is idempotent; terminal transitions are stable.
- [ ] Implement ASP.NET routes, strict DTO validation, safe error envelope, exact-origin CORS and Idempotency-Key support. Test missing/expired/forged/wrong-issuer/wrong-audience JWTs and cross-owner 404s. Cognito is proposed; do not treat old Better Auth cookies as valid or put a secret in desktop clients.
- [ ] Test empty artifact listing before publication, signed download refresh and optional `/api/dubs` compatibility over AWS records. Test app DI graph excludes SDK/Composition/inference packages; no local in-memory accepted queue or Cloudflare dependency.
- [ ] Run portable Release builds and `dotnet test tests/Trackdub.Api.Tests -m:1` / `dotnet test tests/Trackdub.Cloud.Aws.Tests -m:1`. Use fakes plus a local DynamoDB integration fixture for transaction semantics where available; do not claim local S3 emulation validates real IAM or AWS service behavior. Commit reviewed changes; no deploy or resource creation.

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

## Task 3: AWS dispatcher, conditional execution claims and reconciliation

**Create:** `src/Trackdub.Cloud/Jobs/JobDispatcher.cs`, `JobReconciler.cs`, `IJobScheduler.cs`, `IJobControlStore.cs`; `src/Trackdub.Cloud.Aws/Jobs/BatchJobScheduler.cs` (or only `EcsJobScheduler.cs` if selected), `DynamoDbJobControlStore.cs`; `src/Trackdub.Api/Hosting/JobControlBackgroundService.cs`; tests `tests/Trackdub.Cloud.Tests/JobDispatcherTests.cs`, `JobReconcilerTests.cs`, and claim tests in `tests/Trackdub.Cloud.Aws.Tests/`.

**Modify:** API DI/configuration and shared AWS store from Task 1. AWS SDK clients use task-role credentials/default chain; no persisted access keys or cross-cloud scheduler bridge.

**Consumes:** Task 1 store/contracts and proposed task-role boundaries.
**Produces:** bounded dispatch/reconciliation BackgroundService in the same CPU API host, conditional execution claims, fenced heartbeat/candidate publication and queue/deadline/cancel handling. API replica restarts cannot lose accepted jobs.

- [ ] Write fake-scheduler tests for API restart after acceptance, outage before submit, lost submit response, lease recovery and repeated dispatch. With Batch, simulate duplicate execution IDs for one dispatch and assert only one claim succeeds before CLI starts. With ECS, assert stable clientToken and pinned definition revision.
- [ ] Implement transactional/conditional DynamoDB outbox claims and fixed scheduler request generation. GSI discovery may lag; re-query and conditionally claim base-table records rather than inferring absence or uniqueness from the index. Bound each loop and safely stop on API shutdown.
- [ ] Implement shared task-role-backed claim/heartbeat/progress/candidate mutations. Reject mismatched execution/generation, expired lease, stale attempts and replayed sequences. Do not expose worker control to browser JWTs; document that trusted POC worker IAM is not per-tenant isolation.
- [ ] Test lease expiry does not authorize replacement until the previous execution is confirmed stopped; loss of lease makes adapter stop. Default heartbeat 15 seconds and lease 90 seconds, unless Task 0 changes them.
- [ ] Test uploaded result/candidate still needs scheduler exit 0 and required S3 version/hash/size verification before final CAS. Printed success followed by exit 139, missing result or partial upload cannot become succeeded.
- [ ] Test queue/startup/run deadlines, cancel-versus-terminate race, repeated/out-of-order observations, cancellation/completion race and lost candidate write. Keep one retry owner: Batch attempts=1; maximum two logical attempts only for approved infrastructure/staging failures.
- [ ] Run focused cloud/store/API tests using fakes and local fixtures; record real IAM/service checks for approved AWS acceptance. Commit code/config drafts only; do not invoke remote scheduler or provision resources.

## Task 4: One-shot supervisor and artifact publication

**Create (core):** `src/Trackdub.CloudJobAdapter/Trackdub.CloudJobAdapter.csproj`, `Program.cs`, `JobAdapter.cs`, `JobExecutionIdentity.cs`, `AwsJobControlStore.cs`, `CliProcessRunner.cs`, `ArtifactPublisher.cs`, `TransferClient.cs`, `ProcessShutdown.cs`; `tests/Trackdub.CloudJobAdapter.Tests/` covering supervision and protocol; `Dockerfile.cloud-worker`.

**Modify:** `Trackdub.slnx` and/or a portable solution filter; `.dockerignore` only if needed for the new build; `.github/workflows/container-image.yml` to build/verify the adapter profile after approved CLI machine changes. Keep existing server/rtx outputs.

**Consumes:** Task 2 strict process protocol and Task 3 shared claim/control store.
**Produces:** one supervisor that wraps the existing CLI; fixed argv, isolated filesystem, hash-verified transfers, candidate evidence and correct process exit. Scheduler success is provisional until durable job finalization.

- [ ] Write subprocess-fixture tests: normal success; exit 0 without result; failed result; printed/written success then exit 139; hang; exit 137 with/without durable cancel intent. None may parse human output for outcome.
- [ ] Implement claim-before-work; private per-attempt input/project/tmp paths; `ProcessStartInfo.ArgumentList` with fixed CLI path and translated approved preset only. Test quotes/semicolon/command-substitution strings remain data or are rejected; no shell is involved. Reject symlink/relative-path escape in artifacts.
- [ ] Verify input checksum/size and ffprobe metadata. Enforce execution timeout; heartbeat while downloading/running/publishing. If control lease cannot be renewed before expiry, stop the CLI and decline publication. Do not grant a replacement while the old execution might run.
- [ ] Tail dedicated progress JSONL, throttle snapshots, retain run IDs/sequences, and report through task-role-backed, fenced DynamoDB attempt control. Stage percent stays nullable; never average stage values into a fabricated whole-job percent.
- [ ] Upload allowlisted export/subtitles/strict result to immutable attempt keys. Test interruption after each upload and loss of final outcome write; retry transfers/outcome writes without restarting CLI. Reconciler recovers from candidate/result objects. Close project connections and use safe SQLite backup if project snapshot is selected in Task 0.
- [ ] Implement SIGTERM forwarding with 20-second internal grace inside 30-second stop window, then process-tree termination/reaping. Test cooperative and hung child; terminal state derives from intent/deadline/scheduler cessation, not signal number alone.
- [ ] Create a derived adapter image from the approved digest, copy only supervisor publish output, retain non-root ownership and CLI dependencies, override its entrypoint explicitly. Test an approved no-GPU fake workload in CI; no weights/GPU acceptance is claimed.
- [ ] Run adapter/portable CLI builds and focused tests. Verify both original image profiles still package as before; do not broaden testing without a new failure or concern. Commit reviewed files. Do not push a billable build or run target GPU here.

## Task 5: Scheduler/IaC drafts and honest operator runbook

**Modify (core):** `docs/operations/cloud-gpu-container.md`, `.env.example`, `docker-compose.aws.yml` and `docker-compose.yml` only for verified effective options, `scripts/docker/smoke-gpu-matrix.sh`; corresponding existing script tests if present.

**Create:** `docs/operations/snapsoft-poc-acceptance.md`, `scripts/docker/verify-cloud-job-artifacts.sh` (or equivalent native validation tool); proposed SnapSoft-owned IaC path `infra/snapsoft-poc/` only after repo ownership/tooling is agreed.

**Correct:** stale references in `docs/development/TROUBLESHOOTING.md`, aggregate `development.md`, readiness spec/ADR and their aggregates; annotate historical split plans. Do not rewrite unrelated documentation or modify aggregate outputs without identifying their generation convention.

**Consumes:** approved service/host/image/preset and code contracts from Tasks 0–4.
**Produces:** reviewable resource/task definitions, one canonical server runbook and acceptance harness; no resources applied.

- [ ] Draft CPU API ECS/Fargate service/HTTPS/ALB, Cognito client registration, DynamoDB table/index layout, private versioned/encrypted S3 buckets and their lifecycle. Also draft GPU reservation, fixed image/definition, task execution versus application role, log retention, queue/deadline/retry settings, host capacity ceiling, EBS mount/UID and seed/warm bootstrap barrier. Prevent scheduling until preparation succeeds. Specify whether capacity stays warm or scales down; container exit alone does not stop instance billing.
- [ ] Document registry pull credentials/ECR mirroring and exact digest mapping; CPU API/dispatcher task role, GPU adapter task role, Cognito validation and S3 transfer permissions; outbound network access and secret rotation. SnapSoft confirms the integration fits funding/SOW.
- [ ] Keep prepared model/engine storage separate from job workspaces. Define container-replacement durability and optional same-AZ host reattachment; avoid a fleet-shared WAL DB or concurrent model-index mutation. Do not claim host-loss persistence from an EBS bind mount alone.
- [ ] Fix smoke verdict aggregation to retain inference verdict and exit health separately; process exit 139 must fail acceptance even when JSON says passed. Test zero/failed/no-JSON/crash-after-success cases with canned outputs, without GPU execution. A matrix containing failed required cells must not return overall acceptance success.
- [ ] Replace stale API `.env`/Swagger/compose claims with distinct CLI controls and the proposed AWS CPU API contract; identify the Cloudflare scaffold as historical for this POC; spell out actual `/data/models` settings/index/log roots and project-local SQLite. Align seed/warm docs with AWS server override and pinned digest.
- [ ] Validate shell syntax and dry-run rendering; lint IaC locally using the selected tool without provider apply. Review permissions and mounts against actual code. Commit only agreed docs/templates. Deployment remains a later, explicit review step.

## Task 6: Approved-environment functional acceptance and handoff

**Files:** acceptance checklist/evidence manifest under `docs/operations/snapsoft-poc-acceptance.md`; runtime evidence goes to the agreed access-controlled artifact location, with links/hashes and no credentials.

**Consumes:** accepted Tasks 0–5, separately authorized AWS environment/resources, known fixture and pinned releases.
**Produces:** reproducible pass/fail record for every checkbox in spec section 7 and signed-off ownership/runbook handoff.

- [ ] Verify all operational API/job/media paths use AWS, including auth and presigned S3 transfers; demonstrate independence from Cloudflare API/D1/R2 and the agreed portal/legacy cutover policy. Verify actual IAM denies unauthorized actions.
- [ ] Run the agreed target-GPU seed/provider/admission/cache verification and E2E fixture; capture actual process exits plus structured evidence. Confirm image pull on that host.
- [ ] Exercise durable API restart, duplicate submit/lost scheduler response, failure, queued/running cancel, abnormal native exit, timeout, container replacement, retrievable artifacts and foreign-owner denial.
- [ ] Record all source/image/definition/model/host identities; verify downloaded checksums and nonempty translated speech, not merely successful export. Document any stage degradation or limitation as a failed/unmet criterion unless explicitly allowed beforehand.
- [ ] Confirm no formal benchmark/cost-analysis deliverable was added. Record diagnostic durations only as context; do not turn them into throughput/cost claims.
- [ ] Hand off task/job definitions, secret/registry setup procedure, canonical runbook, acceptance evidence, known limitations and proposed-versus-confirmed responsibilities. Do not mark target readiness complete until this evidence exists.

## Review status and approval gate

Self-review checked source/design separation, all eight requested deliverables, API compatibility, acceptance atomicity, duplicate execution, cancellation races, retries/deadlines, artifact finalization and infrastructure ownership. The unresolved choices are explicitly listed in the spec and Task 0; they are not hidden implementation placeholders.

The plan is ready for Michael's review. The approval gate comes directly from the pasted request: **“Do not begin implementation until the plan is approved.”** No code, infrastructure or external service changes should be made before that approval. AWS-only API/storage is confirmed. Confirm the remaining hosting/auth/cutover and SnapSoft responsibility/service choices before dependent tasks begin.
