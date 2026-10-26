# Validation gates

A gate is a command with a recorded exit code. "Builds locally" is not a gate result.

## Gate order (verbatim from `AGENTS.md`)

Run in order. Do not reorder, do not parallelize, do not skip a gate because a previous one looked fine.

```bash
# Gate 1 — restore
dotnet restore Trackdub.slnx -m:1

# Gate 2 — Release build, warnings as errors
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror

# Gate 3 — Release tests, no rebuild
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
```

Format gate (`CONTRIBUTING.md`, and job `format` in `.github/workflows/ci.yml`):

```bash
dotnet format Trackdub.slnx --verify-no-changes
```

Resolve and verify `BASE_REF` from PR metadata or the supplied base ref before running diff-dependent checks. CI formats changed C# files with `--include` from the committed base-to-HEAD diff; validation must also include worktree changes from `git diff --name-only HEAD -- '*.cs'`. If no base ref is available, report committed-branch checks as `NOT VERIFIED`; full-solution formatting does not replace those checks.

### Why each flag

| Flag | Reason |
|---|---|
| `-m:1` | single-node MSBuild. Multi-node builds race on shared `bin`/`obj` output in this repo layout and produce intermittent failures that look like real errors. Applies to `restore`, `build`, and `test`. |
| `--no-restore` (Gate 2) | restore already ran in Gate 1. Re-restoring mid-gate can silently change the resolved graph, which invalidates the lock-file story and the `-warnaserror` result. |
| `--no-build` (Gate 3) | Gate 2 produced the Release binaries. Rebuilding inside `test` re-runs the compiler, so a warning-as-error failure surfaces as a *test* failure and the reported exit code no longer identifies the real cause. |
| `--configuration Release` | Debug is not the shipping configuration. Analyzer and warning coverage differs, and Release-only codegen paths are untested otherwise. |
| `-warnaserror` | `Directory.Build.props` sets `TreatWarningsAsErrors=true`, but passing `-warnaserror` explicitly makes the gate hold even if a project or an environment overrides the property. |

`WarningsNotAsErrors` carries `RS0030` (the `Path.Combine` banned-API diagnostic) precisely because a small number of existing call sites remain. **The build will not fail on `Path.Combine`.** Count it yourself.

## Non-build gates

These are not part of the `dotnet` chain but are part of CI and must be reported.

### 4a — Dependency direction

Every edge in the `AGENTS.md` "Dependency Architecture" graph must match the real `<ProjectReference>` sets under `src/**.csproj`. `Domain`, `Licensing`, `Analyzers`, and `OnnxRuntime.Dnnl.Native` reference nothing. `Contracts` references only `Domain` (ADR-0011). No inference type may leak upward into Domain or Contracts.

Run this **and** 4b. `DependencyGraphTests` is the authoritative gate — it compares the
`AGENTS.md` diagram against every csproj in both directions and fails CI on a mismatch. The
script below is a fast standalone pre-check; it does not replace the test, and a passing
script with a failing `DependencyGraphTests` is a FAIL, not a pass.

```bash
python3 tools/ci/verify-dependency-graph.py
```

`python3` may not be on `PATH` on Windows (`python` may be the only alias, or neither). If
neither works, skip the script and run `DependencyGraphTests` from 4b — the test is the
authoritative gate, so skipping the pre-check is acceptable while skipping the test is not.

If the two disagree, trust `DependencyGraphTests` and treat it as a script or diagram bug to
report. Never reconcile them by editing the diagram to match whatever passed.

### 4b — Architecture tests

`tests/Trackdub.Architecture.Tests/` — runs inside Gate 3; run explicitly when Gate 3 was skipped:

```bash
dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1
```

| File | Enforces |
|---|---|
| `DependencyGraphTests.cs` | `AGENTS.md` diagram ↔ csproj parity (both directions), `DomainHasNoProjectReferences`, `ContractsReferencesOnlyDomain`, acyclicity, WinML-not-DirectML package/target invariants, DNNL native RID dirs + checksum provenance template, `TrackdubOrtRuntimeFlavor` asset-copy conditions in both directions, `Inference.Onnx` using `Trackdub.Contracts.ApplicationContracts` (not the root Contracts namespace), portable RID graphs in four `packages.lock.json` files |
| `LicensingIsolationTests.cs` | zero `ProjectReference`; no third-party crypto packages (BouncyCastle, jose-jwt, IdentityModel, NSec, libsodium, …); single-target `net10.0`, no multi-targeting |
| `StageNameConsistencyTests.cs` | no inline stage-name literal at any `StageRunRecord.Start` call site; `KnownStageNameValues` covers every constant in `StageNames.cs` |
| `WorkflowTriggerTests.cs` | `pull_request` triggers in `ci.yml`, `model-audit.yml`, `benchmark-report-validation.yml` carry no `branches:` filter (stacked-PR CI); `paths:` filters stay allowed |

### 4c — `BannedSymbols.txt`

`Microsoft.CodeAnalysis.BannedApiAnalyzers` is wired repo-wide through `Directory.Build.props` (`AdditionalFiles`) with `BannedSymbols.txt` listing the four `System.IO.Path.Combine` overloads. Use `Path.Join` in new or changed code. `Path.Combine` silently drops earlier segments when a later argument is rooted; `Path.Join` has no reset behavior.

Count only **added** lines (`-U0` makes every hunk line a pure addition), so pre-existing occurrences on untouched lines are never mixed in. `--` before the pathspec keeps a leading-dash ref from being read as an option.

```bash
BASE_REF=origin/main   # or the PR base ref
git diff -U0 "$BASE_REF"...HEAD -- '*.cs' | grep -E '^\+' | grep -v '^+++' | grep -c 'Path\.Combine'   # committed changes
git diff -U0 HEAD -- '*.cs'             | grep -E '^\+' | grep -v '^+++' | grep -c 'Path\.Combine'   # uncommitted changes
```

Any count above 0 = FAIL the gate; each added line is a finding. To enumerate them rather than count them, drop the trailing `-c`.

Grep this way rather than `xargs grep -c`: `xargs` is a GNU extension (unavailable on stock macOS), and batching whole files with `grep -c` mixes new and pre-existing hits in one number and omits the filename.

### 4d — Analyzers

`src/Trackdub.Analyzers/` ships `WavePcm16MultiSourceMixOptInAnalyzer` → diagnostic **`TRACKDUB001`**, `DiagnosticSeverity.Warning`, enabled by default. It fires when a method whose name contains `Mix`, `Mixer`, `Blend`, or `Render` calls `Trackdub.Media.Waveforms.WavePcm16.WriteSamplesAsync` without a literal `normalizePeak: true` (cumulative mixes hard-clip to `short.MaxValue`; ADR-0012).

`Trackdub.Media` consumes it via `OutputItemType="Analyzer"` with `ReferenceOutputAssembly="false"`.

Report: analyzer diagnostics active for the touched projects, and no **new** diagnostic IDs in Gate 2 output. Tests: `tests/Trackdub.Analyzers.Tests/WavePcm16AnalyzerTests.cs`.

### 4e — `packages.lock.json` integrity

```bash
git diff --name-only "$BASE_REF"...HEAD -- '*packages.lock.json'
git diff --name-only HEAD -- '*packages.lock.json'
git diff --check "$BASE_REF"...HEAD -- '*packages.lock.json'
git diff --check HEAD -- '*packages.lock.json'
```

- A hand-merged lock file is a hard FAIL. Lock files are regenerated, never edited.
- Any lock diff must be reproducible by `dotnet restore Trackdub.slnx --force-evaluate -m:1`.
- Conflict markers: take a side, then regenerate — never hand-resolve.
  ```bash
  git checkout --ours -- <path>/packages.lock.json     # or --theirs
  dotnet restore Trackdub.slnx --force-evaluate -m:1
  ```
- Portable RID graphs must remain intact: `net10.0/{win-x64,win-arm64,linux-x64,linux-arm64,osx-x64,osx-arm64}` in `src/Trackdub.Contracts`, `src/Trackdub.Domain`, `src/Trackdub.Inference`; plus `net10.0-windows10.0.19041/{rid}` for `src/Trackdub.Inference.Onnx`. Asserted by `OnnxLockFilePreservesPortableRuntimeIdentifierGraphs` (Windows-only).

### 4f — Repository boundary and audit mirrors (required CI jobs)

```bash
python3 scripts/ci/check-repository-boundary.py    # stale license / desktop-boundary claims
python3 scripts/ci/check-audit-mirrors.py         # concatenated audit matches its copies
```

### 4g — Controlled-matrix CPU budget (required CI job)

```bash
dotnet build src/Trackdub.Benchmarks.DevHost -c Release --no-restore -f net10.0 -m:1
python3 scripts/ci/check_controlled_matrix_cpu_budget.py
```

Run this job for every validation, not only when benchmark files changed.

### 4h — Model manifest (when models changed)

```bash
python tools/ci/validate-manifest-schema.py
python tools/ci/audit-bundled-model-manifest.py
python tools/ci/verify-manifest-hashes.py --structural --all-families
```

Also gated by `tools/validation/validate-repo.ts` (Deno; targets `all`, `manifest`, `openapi`). Python validators are the CI gate.

## Scoping

- **Whole-solution (default):** Gates 1–5.
- **Narrowed to one test project** (e.g. `/validate Trackdub.Application.Tests`): Gates 1–3 may narrow, but the output must state verbatim that the solution-wide Release gate was skipped and why. Gates 4 and 5 still run at solution scope.

## Rules

1. **On any failure: stop.** Report the first failing gate with the exact command and a verbatim output excerpt sufficient to locate the error. Never paraphrase a failure as success, never continue past a failure to produce a PASS table.
2. **No completion claims on partial evidence.** Anything not executed is `NOT VERIFIED` — never PASS, never "expected to pass".
3. **Never infer a gate result** from a previous run's cache, a green CI badge, or a comment.
4. **A gate that cannot run** (missing SDK/workload/model fixture, timeout) is `NOT VERIFIED` with the blocking reason and the command that unblocks it.
5. **Do not modify source to make a gate pass.** Report; the human decides.
6. Debug builds never substitute for the Release gate.

## Report template

| # | Gate | Command | Result | Evidence |
|---|------|---------|--------|----------|
| 1 | restore | `dotnet restore Trackdub.slnx -m:1` | PASS / FAIL / NOT VERIFIED | exit code |
| 2 | Release build `-warnaserror` | `dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror` | | warning/error count |
| 3 | Release tests | `dotnet test Trackdub.slnx --configuration Release --no-build -m:1` | | passed/failed/skipped counts |
| 4a | dependency direction | `python tools/ci/verify-dependency-graph.py` | | offending edges |
| 4b | Architecture.Tests | `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1` | | failing test names |
| 4c | BannedSymbols | `git diff -U0 "$BASE_REF"...HEAD -- '*.cs'` plus `git diff -U0 HEAD -- '*.cs'` | | new/modified `Path.Combine` lines |
| 4d | Analyzers | Gate 2 output | | new diagnostic IDs |
| 4e | `packages.lock.json` integrity | committed and worktree diff commands above | | conflicting paths |
| 4f | repository boundary / audit mirrors | `python3 scripts/ci/check-repository-boundary.py`; `python3 scripts/ci/check-audit-mirrors.py` | | offending lines |
| 4g | controlled-matrix CPU budget | build Benchmarks.DevHost; run `python3 scripts/ci/check_controlled_matrix_cpu_budget.py` | | budget result |
| 4h | model manifest (if applicable) | 3 python validators | | error/warning counts |
| 5 | format | `dotnet format Trackdub.slnx --verify-no-changes` | | changed files |

**Verdict line:** `VALIDATION: PASS` or `VALIDATION: FAIL (first failing gate: <N>)`, or `VALIDATION: NOT VERIFIED (gates: <list>)`.

Verbatim failure excerpt goes below the table. Use `context/templates/evidence-report.md` for the full shape.

## Related

- `context/standards/architecture-rules.md` — what the boundary gates are protecting
- `context/standards/coding-standards.md` — `Path.Join`, warning discipline
- `context/templates/evidence-report.md` — report shapes
- `context/processes/adding-pipeline-stage.md` — step 9 uses this gate