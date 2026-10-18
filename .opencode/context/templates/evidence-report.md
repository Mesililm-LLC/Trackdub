# Evidence report templates

Three shapes: full readiness/evidence report, short benchmark result, handoff. Copy the block, delete nothing — an empty section means "none", and the **NOT VERIFIED** section is never deleted.

Rules that apply to all three: measurements and inference live in separate sections; every claim names the command that produced it; `NOT VERIFIED` is a valid, complete answer.

---

## 1. Readiness / evidence report (full)

````markdown
# Evidence — <what this proves>

**Repo:** current checkout (`git rev-parse --show-toplevel`)
**Branch:** <branch>   **Commit:** `<sha>`
**Date:** <YYYY-MM-DD>   **Host:** <os> / <rid>
**Scope:** whole solution | narrowed to <project> — <reason>

## What was run

| # | Command | Working dir | Exit code | Duration |
|---|---------|-------------|-----------|----------|
| 1 | `dotnet restore Trackdub.slnx -m:1` | repo root | 0 | |
| 2 | `dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror` | repo root | 0 | |
| 3 | `dotnet test Trackdub.slnx --configuration Release --no-build -m:1` | repo root | 0 | |
| 4 | `dotnet format Trackdub.slnx --verify-no-changes` | repo root | 0 | |
| 5 | `python tools/ci/verify-dependency-graph.py` | repo root | 0 | |
| 6 | `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1` | repo root | 0 | |
| 7 | `<execution / benchmark command>` | | | |

## Build output

```
<verbatim excerpt — warnings, errors, analyzer diagnostics>
```

Warnings: <n>   Errors: <n>   New diagnostic IDs: <none | list>
`Path.Combine` hits in touched files: <n> (RS0030 is warning-only; new hits = FAIL)

## Test output

```
<verbatim excerpt>
```

Passed: <n>   Failed: <n>   Skipped: <n>
`Trackdub.Architecture.Tests`: <n>/<n> passing — list every test class touched by the change

## Readiness ladder

Each rung stated individually. Do not merge rows.

| Rung | State | Evidence |
|------|-------|----------|
| provider registered | | |
| runtime installed | | |
| model downloaded | | |
| checksum verified | | |
| license recorded | | |
| license reviewed | | |
| commercial-use decision | | |
| provider available (EP) | | |
| stage enabled in run | | |
| stage ran | | |
| output usable | | |

## Artifacts

| Kind | Path | Sha256 | Produced by |
|------|------|--------|-------------|
| | | | |

## Stage results

| Stage | Status | Reason code | Duration | Notes |
|-------|--------|-------------|----------|-------|
| vad | | | | |

`skipped` ≠ `succeeded`. Call out every resume (`EXISTING_ARTIFACTS_VALID`) and every
`DISABLED_BY_OPTION` explicitly — a stage that did not run has no timing evidence.

## NOT VERIFIED

- <item> — <why: missing toolchain / no GPU / no fixture / not executed> — <command that would resolve it>

## Verdict

VALIDATION: PASS
VALIDATION: FAIL (first failing gate: <N>)
VALIDATION: NOT VERIFIED (gates: <list>)

First failing gate, verbatim:
```
<exact command>
<exact output lines — enough to locate the error, no paraphrase>
```
````

---

## 2. Benchmark result (short)

````markdown
# Benchmark — <scenario>

**Commit:** `<sha>`   **Baseline commit:** `<sha>` (or `MISSING BASELINE`)
**Host:** <cpu/gpu model>, <os>, <rid>   **Mode:** Release
**Warmup / iterations:** <n> / <n>   **Fixture:** <path> (+ sha256)
**Command:** `<exact command>`

## Results

| Metric | Baseline | Current | Delta | Like-for-like |
|--------|----------|---------|-------|---------------|
| | | | | yes/no |

**Report artifact:** <path to BenchmarkEvidenceReport JSON>

## Attribution

| Change | Expected effect | Observed | Isolated by |
|--------|-----------------|----------|-----------|
| | | | |

## Constraints

- Same fixture, same provider, same model, same variant? <yes/no>
- Same execution provider in both runs? <yes/no> — EP used: <kind>
- Machine state comparable? <power plan, thermal, background load>
- Run-to-run variance: <n repeat runs, spread>

## NOT VERIFIED

- <item> — <why>

No performance claim may be made from a run without a recorded baseline commit.
A compared run whose Git revision was not recorded cannot be compared.
````

---

## 3. Handoff report

````markdown
# Handoff — <one-line summary>

## What changed

| Layer | Files | What |
|-------|-------|------|
| Domain | | |
| Contracts | | |
| Application | | |
| Inference / Inference.Onnx | | |
| Infrastructure / Media | | |
| Composition | | |
| Tests | | |
| Docs | | |
| Manifest / lock files | | |

Deliberately **not** changed: <scope that was intentionally left alone>

## What was validated

| Gate | Command | Result |
|------|---------|--------|
| restore | `dotnet restore Trackdub.slnx -m:1` | PASS |
| Release build | `dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror` | PASS |
| Release tests | `dotnet test Trackdub.slnx --configuration Release --no-build -m:1` | PASS |
| format | `dotnet format Trackdub.slnx --verify-no-changes` | PASS |
| dep graph | `python tools/ci/verify-dependency-graph.py` | PASS |
| arch tests | `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1` | PASS |

Verdict: `VALIDATION: PASS`

## Model / provider impact

Rungs climbed: <list>
Rungs **not** climbed: <list>

## What remains

- [ ] <next step, with owner>
- [ ] <follow-up issue, `TS-xxx` if one exists>

## Submodule pin

**Not applicable** — this repo does not own a submodule. (If this handoff affects the gated
consumer, the pin move happens in `Trackdub-gated` at `external/Trackdub`; see
`context/processes/submodule-pin-bump.md`.)

Current gated pin: <sha / tag>  — verify with:
```bash
cd ../Trackdub-gated && git submodule status
```

## PR description (ready to paste)

```
<title: imperative — Add / Fix / Remove ...>

<body: what changed, by layer; gate result with commands; model/readiness impact;
explicit NOT VERIFIED list; issue reference if the user provided one>
```

## Issue tracking (none by default)

Do not use Linear or require issue searches/status/TS IDs; track requested work locally in plans/Git unless the user explicitly asks for an external issue tracker.

## NOT VERIFIED

- <item> — <why> — <command that resolves it>

or: `none`
````

---

## Wording rules

| Instead of | Write |
|---|---|
| "works" | "Gate 2 exit 0; Gate 3 412 passed / 0 failed" |
| "GPU enabled" | "EP used: `<kind>`, from `<StageRuntimePlan>`" |
| "model downloaded" | "files present at `<path>`; hashes match per `<command>`" |
| "stage succeeded" | "ran; artifact at `<path>`; degraded: `<DegradationCode>`" |
| "fast" | "`<metric>` `<value>` vs baseline `<value>` (`<sha>`)" |
| "should pass" | `NOT VERIFIED` |
| "tests pass locally" | "Release gate passed locally; CI run `<id>`: <status>" |

An empty **NOT VERIFIED** section is allowed only when every claim above it is backed by a recorded command and exit code. When in doubt, list the item.

## Related

- `context/standards/validation-gates.md` — gate definitions and the verdict line
- `context/standards/architecture-rules.md` — readiness ladder and the distinction pairs
- `context/processes/pr-lifecycle.md` — where a handoff report is consumed