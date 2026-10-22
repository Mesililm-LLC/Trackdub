# SnapSoft application implementation: local verification

Date: 2026-10-21. Branch: `codex/snapsoft-application`.
Starting core commit: `f2438ced22dc0c6fd74d724f2719bfe6748641e3` (clean tree).
AWS-only planning revision `60c8c161` was present. Application implementation was
explicitly approved in the conversation; AWS provisioning/deployment was not.

## Changed application surfaces

- CLI machine protocol, built-in `poc-server-v1`, shared variant/provider options,
  strict result serialization and SIGTERM cancellation: `src/Trackdub.Cli`,
  `src/Trackdub.Contracts/Jobs`, and the existing shared application execution path.
- Typed job/upload/result/error/claim contracts, orchestration, cancellation and media
  validation: `src/Trackdub.Cloud`.
- Transactional DynamoDB acceptance/CAS and versioned/checksummed S3 transfers:
  `src/Trackdub.Cloud.Aws`.
- JWT-protected CPU endpoints and bounded host loop: `src/Trackdub.Api`.
- One-shot process supervision, independent lease watchdog, host/attempt isolation,
  publication retries and native artifact checker: `src/Trackdub.CloudJobAdapter`.
- New cloud/API/store/supervisor tests and test-only process/scheduler/storage fakes.
- CPU/derived-worker Dockerfiles with narrow build contexts; accurate environment
  example, CLI compose cleanup, historical documentation corrections and matching aggregates.
- Licensed English speech fixture, schema/request examples, seed list, pinned manifest
  descriptor and the [SnapSoft acceptance/permission contract](snapsoft-poc-acceptance.md).

Implementation commits: `90b58399` (strict CLI/preset), `94a2715f`
(AWS API/control/supervisor), `e5ffe644` (model-scoped variant precedence and fixture casing).
The final documentation/container commit contains this handoff and acceptance assets.
Use `git diff --name-only f2438ced..HEAD` for the complete changed-file inventory.

## Checks actually run

| Check | Result |
|---|---|
| Baseline Domain suite | 173 passed |
| Cloud rules, submission, upload, dispatch and reconciliation | 20 passed, Release |
| API JWT/ownership/acceptance/disabled intake | 11 passed, Release |
| AWS tests | 2 passed, Release: actual DynamoDB Local transactions/CAS and offline S3 signing |
| Supervisor/process/artifact-checker tests | 10 passed, Release, Windows and CPU-only Linux |
| Shared pipeline/model-selection tests | 51 passed |
| Relevant SDK/CLI/locking/manifest/cancellation tests | 52 passed; 8 Unix-only lock tests skipped on Windows |
| Architecture suite, including CPU transitive dependency checks | 23 passed |
| Portable CLI Release build, warnings as errors | Passed, zero warnings/errors |
| Supervisor Release build, warnings as errors | Passed, zero warnings/errors |
| CPU API Docker build | Passed locally; no ONNX filenames in published app directory |
| Derived supervisor Dockerfile check | Completed; intentional no-default SERVER_IMAGE lint warning |
| Compose rendering and Bash syntax | Passed |
| Smoke-matrix regression script | Passed: normal exit, crash after success, failed verdict, missing JSON and zero attempts |

The 169 targeted passing .NET tests above exclude the baseline Domain suite and do not
double-count the repeated Linux execution. No full solution/GPU/real-model suite was run.
Tests use the source's shared engine and machine boundary where applicable, and a clearly
named fake pipeline for supervisor integration. Copying a fixture in that fake is only
protocol/transfer evidence, never evidence of ASR, translation, TTS or dubbing quality.

DynamoDB Local ran in a disposable loopback-only container, image
`amazon/dynamodb-local@sha256:6ada631ec7aa5fc83446855eb17233e3dad184fec47477e8140c6c569cfe0c86`.
Its tests prohibit non-loopback service URLs. They verified transaction rollback leaves
no partial records, persistent idempotency lookup after store recreation, competing CAS
winners and rejection of expired worker leases. This does not validate real AWS IAM.
S3 presigning uses public dummy local credentials offline and asserts signed Content-Length
and SHA256 headers. Actual S3 service enforcement remains an AWS acceptance step.

Regression failures were observed before fixes for unsupported CLI machine flags,
duplicate claim/cancellation/lease rules, saved variant precedence, and cancellation
without scheduler cessation evidence. Final inspection and one independent source review
also identified signed upload-size enforcement, child startup cleanup and stalled-renewal
risks; those were fixed and covered locally.

Reproduction commands:

```sh
dotnet test tests/Trackdub.Cloud.Tests -c Release -m:1
dotnet test tests/Trackdub.Api.Tests -c Release -m:1
docker run -d --rm --name trackdub-dynamodb-local -p 18000:8000 amazon/dynamodb-local
TRACKDUB_DYNAMODB_LOCAL=http://127.0.0.1:18000 dotnet test tests/Trackdub.Cloud.Aws.Tests -c Release -m:1
docker stop trackdub-dynamodb-local
dotnet test tests/Trackdub.CloudJobAdapter.Tests -c Release -m:1
dotnet test tests/Trackdub.Architecture.Tests -m:1
dotnet test tests/Trackdub.Application.Tests -m:1 --filter 'FullyQualifiedName~DubbingPipelineEngineTests|FullyQualifiedName~RuntimeModelRequestFactory'
dotnet test tests/Trackdub.Sdk.Tests -f net10.0 -m:1 --filter 'FullyQualifiedName~CliMachineBoundaryTests|FullyQualifiedName~RunPipelineHandlerGoalTests|FullyQualifiedName~CancellationPropagationTests|FullyQualifiedName~ProjectLockTests|FullyQualifiedName~RunManifestWriterTests'
dotnet build src/Trackdub.Cli -f net10.0 -c Release -m:1 -warnaserror
dotnet build src/Trackdub.CloudJobAdapter -c Release -m:1 -warnaserror
bash scripts/docker/tests/smoke-gpu-matrix.test.sh
docker build -f Dockerfile.api -t trackdub-cloud-api:local .
```

## Remaining integration gates

No concrete Batch/ECS adapter was selected or implemented speculatively. Intake remains
disabled without one. SnapSoft must confirm that selection, fixed definition/identity
mapping and stop/discovery semantics. No scheduler/job definitions, IAM, Cognito clients,
GPU hosts, EBS, registry changes or IaC were applied.

The derived supervisor image was not built against an approved newly published server
digest. Its build checks both CLI machine flags and normal help exit; an older published
CLI image cannot silently qualify. Existing server/rtx image sources and entrypoints remain.

Live Cognito/PKCE, S3 uploads/downloads/IAM, production DynamoDB, target scheduler, target
GPU model/provider/voice execution, operational persistence and portal cutover remain
unverified. No paid workload, AWS resource creation, deployment, remote push, CI rerun,
formal benchmark or cost-analysis deliverable was performed.
