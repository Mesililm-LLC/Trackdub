# ADR-0015: Pipeline transient telemetry — in-process only on on-prem tiers

> Current AWS POC contract: [SnapSoft application acceptance](../operations/snapsoft-poc-acceptance.md). Historical API/Swagger/compose/telemetry examples below are not evidence of current wiring. The new CPU API uses DynamoDB/S3, contains no inference, and has no billing/OTel exporter. GPU compose runs the one-shot CLI; it does not expose HTTP.

- Status: Draft (promoted from `docs/internal/pipeline-readiness-spec.md` §9.4)
- Date: 2026-07-23

## Context

Trackdub's `PipelineTransientFaultBus` already exposes a typed, in-process pub/sub surface for stage-level transient failures (see `src/Trackdub.Application/Transcripts/Pipeline/PipelineTransientFaultBus.cs` and the upstream spec `docs/internal/pipeline-readiness-spec.md` §4.3, §9.1). The bus ring-buffers the last 50 faults and surfaces them through `IObservable<PipelineTransientFault>` plus a snapshot accessor; readers today are the diagnostics-bundle exporter (`§9.3`), the per-run aggregation reader (`SnapshotPerRun`, §9.1), the `DubbingPipelineEngine`'s `IAsyncEnumerable<PipelineTransientFault> TransientFaults` accessor (§4.5), and the on-prem tier-specific consumers (Worker metrics, Avalonia `PipelineRunViewModel`, headless SDK).

Earlier drafts assumed a cloud OpenTelemetry/billing surface. Those paths were not current implementation evidence; the new AWS CPU job API does not implement that surface.

The question this ADR promotes is whether the on-prem `Observable` should also bridge to OpenTelemetry, Sentry, or another upstream telemetry sink.

## Decision

**Recommendation (a):** `PipelineTransientFaultBus.Stream` stays in-process only on the on-prem tiers. No new upstream sink on App, SDK, Worker, or CLI. Per-tier consumers (Worker metrics, Avalonia VM, headless SDK) read directly from the bus or its snapshot.

The OTel bridge, when it becomes a customer ask, belongs to the cloud tier (option (c) below) and to a separate ADR. It does NOT belong on the on-prem tiers.

## Alternatives considered

### (a) None on the on-prem tiers — CHOSEN

`Observable` stays in-process; per-tier consumers read directly. No new external sink on `Trackdub.App.Avalonia`, `Trackdub.Sdk`, `Trackdub.Worker`, or `Trackdub.Cli`.

- **Trade-offs.** Cheapest path. Matches the existing on-prem posture: no telemetry surveillance on the end-user runtime (per `AGENTS.md` §Model governance non-negotiables). Preserves the local-first product contract for desktop + on-prem install.
- **When chosen over (b)/(c).** When the customer ask for cross-stage transient-fault telemetry does not yet exist, or when it lives in the cloud tier via (c).

### (b) `IPipelineTransientFaultExporter` adapter + OTel implementation behind a feature flag

Introduce an adapter interface in `Trackdub.Application` and a single OpenTelemetry implementation in `Trackdub.Composition` / `Trackdub.Api`, gated by a `StudioSettings` feature flag.

- **Trade-offs.** Lets the on-prem tiers stream transient-fault records to an OTel collector the user hosts themselves. Consistency with the cloud tier's OTel wiring.
- **Why not chosen.** Invites new infrastructure on a desktop product. Contradicts `AGENTS.md` §Model governance ("no new external telemetry surveillance on the end-user runtime path") — even an opt-in flag can leak through fault-onboarding flows. Adds a new dependency graph edge (`Application` → `OpenTelemetry`) that the on-prem tiers currently avoid. The same surface can be achieved via (c) without forcing the desktop binary to grow.

### (c) No integration on on-prem; centralize on the cloud tier by serializing transient-fault shipments into the existing trackdub-telemetry pipeline

On-prem tiers do nothing; the Cloud API / hosted Trackdub variant consumes `PipelineTransientFault` records end-to-end and reuses the existing `dubbing_metrics` counter surface. Fault shipments into the cloud tier are opt-in per project + per tier via Tier / project settings.

- **Trade-offs.** Preserves the on-prem posture and re-uses what already exists. Hosts transient-fault telemetry where it can be paired with project SLIs/SLOs and per-customer alerts.
- **Promotion** when one of the criteria below holds.

## Consequences

### Positive

- Preserves the no-new-runtime-telemetry posture on desktop + on-prem installs.
- Zero new binary footprint on `Trackdub.App.Avalonia`, `Trackdub.Sdk`, `Trackdub.Worker`, `Trackdub.Cli`.
- Honors the `IObservable<T>` semantics callers already rely on — no opt-in subscription layer to misuse.

### Negative

- A support engineer triaging a desktop crash cannot correlate transient-fault frequency across machines unless the customer opts into cloud-tier tracking.
- The cloud-tier upgrade path is unbounded — when the customer ask lands, a separate ADR will own the OTel adapter surface plus its consumer tests.

### Neutral

- No tests added in this ADR (per spec §11.7 — the surface is held documented but no `Exporter_publishes_to_existing_dubbing_metrics_counter` / `Exporter_does_not_initialize_when_feature_flag_disabled` facts ship until the cloud-tier ADR is opened).
- Cross-link: `docs/internal/pipeline-readiness-spec.md` §9.4 (source) + §11.7 (test surface + promotion gate).

## Promotion criteria

Promote (i.e. open a new ADR that supersedes this one for the cloud-tier OTel bridge) when one or more of the following is true:

1. `Trackdub.Cloud` or a hosted customer SLI/SLO asks for per-stage transient-fault telemetry.
2. A repeat user-facing incident traces to a failure pattern that surface logging alone cannot correlate.
3. The Cloud tier upgrades to consume `PipelineTransientFault` directly and warrants a dedicated bridge.

Per spec §11.7, that future ADR will require two tests (or analogs) at the time of promotion:

- `tests/Trackdub.Api.Tests/Observability/TransientFaultExporterTests.cs::Exporter_publishes_to_existing_dubbing_metrics_counter`
- `tests/Trackdub.Api.Tests/Observability/TransientFaultExporterTests.cs::Exporter_does_not_initialize_when_feature_flag_disabled`

This ADR holds the surface documented so future agents do not re-derive the recommendation from §9.4 verbatim.
