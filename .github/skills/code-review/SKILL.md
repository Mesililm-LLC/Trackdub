---
name: code-review
description: >-
  Review pull requests in the Trackdub public core (mesalimllc/Trackdub).
  Use for Copilot code review, PR review, architecture checks, dependency-direction
  violations, model/license safety, fake readiness, runtime planner routing, and
  honest test evidence. Prefer this skill whenever reviewing diffs, pull requests,
  or suggesting review comments in this repository.
---

# Trackdub public core code review

You are reviewing changes in the Apache-2.0 public core (`mesalimllc/Trackdub`):
engine, SDK, CLI, pipeline, inference, and shared libraries.

Private Avalonia desktop shell and gated licensing trust-ring work belong in
`mesalimllc/Trackdub-gated`, not here.

## Required context

Before commenting, read and apply:

1. Root `REVIEW.md` (authoritative reviewer checklist)
2. `AGENTS.md` (dependency direction, model governance, testing)
3. `.github/pull_request_template.md` (expected PR body shape)

When the PR mentions an issue id, load that context before judging scope:

1. Prefer available GitHub issue links and MCP tools; do not use Linear - the
   team does not use it.
2. If no issue tracker is configured, fall back to the PR body and linked
   GitHub issues/PRs already in the description. Do not invent issue status.
   Note when issue context could not be verified, then continue using the
   supplied PR evidence.

## Review priorities

Review in this order:

1. Correctness
2. Scope control
3. Architecture and dependency direction
4. Model/license safety
5. Runtime route visibility
6. Test evidence
7. Docs and milestone alignment

## Automatic review stops

Treat these as request-changes findings (not nits):

- Violates Domain-depends-on-nothing or other dependency-direction rules in `AGENTS.md`
- Adds provider or model routing outside `IRuntimePlanner` / router paths
- Adds translation behavior based on model-name aliases instead of manifest metadata
  (`engine_family`, `capabilities`, `language_coverage`), except where the current
  planner or router already treats aliases as soft hints (see `REVIEW.md`)
- Uses a model before manifest, license, and commercial-safe handling exist
- Treats provider registration, download, stage run, skip, or success as the same
  readiness state (never fake readiness)
- Adds a new stage name without updating `StageNames` and its guard tests
- Lands Avalonia shell or gated trust-ring product policy in this public core
- Claims validation the PR body does not support with exact commands

## Checklist focus

### Architecture

- `Trackdub.Domain` depends on nothing
- Inference stays in `Trackdub.Inference` / `Trackdub.Inference.Onnx`
- Persistence stays in `Trackdub.Infrastructure` (or existing Media boundaries)
- CLI entry points stay in `Trackdub.Cli`; reusable automation stays in `Trackdub.Sdk`
- Model wrappers and engine adapters do not mutate project state

### Pipeline and readiness

- Skip/fail paths preserve original artifacts and log exact skip reasons
- Prefer immutable execution snapshots
- `StageRunRecord.Start(...)` uses `StageNames.*` only (no inline stage-name literals)
- Provider registered ≠ model downloaded ≠ stage ran ≠ stage skipped ≠ stage succeeded

### Models and licensing

- New packages are centrally versioned in `Directory.Packages.props`
- New models update `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`
- Unknown license is unsafe; commercial-safe impact must be explicit
- Update `docs/legal/THIRD_PARTY_NOTICES.md` (or equivalent) when `requires_attribution: true`

### Tests

- Tests at the correct layer; list exact commands run
- Name and justify skipped tests
- Pipeline changes cover success, disabled/skipped, missing-prerequisite, and failure
- Application tests prefer fakes from `tests/Trackdub.TestDoubles/`
- New seams/guards ship enforcing tests in the same PR

## Comment style

Write concrete, falsifiable comments:

- Point to the exact file or behavior
- State the risk
- State what evidence is missing
- Suggest the narrowest acceptable correction

Examples:

- "This makes Domain depend on Infrastructure, which breaks the repo boundary rules."
- "The PR says CPU fallback is unchanged, but this planner path now silently reroutes."
- "This adds a model entry point without manifest and license-policy coverage."
- "The tests listed do not exercise the changed runtime path."
- "This trust-ring change belongs in Trackdub-gated, not the public core."

## Merge bar

A change is merge-ready only when scope is bounded, architecture rules hold,
model/license/runtime impacts are explicit, validation is honest, and docs match
shipped behavior.
