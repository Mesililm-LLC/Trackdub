# Process — submodule pin bump

## Scope of this file

**This is the core repo's view. This repo does not own a submodule.** `mesalimllc/Trackdub` has no `external/` submodule — it *is* the pinned dependency. The submodule pin lives in the consumer: the sibling `Trackdub-gated` checkout carries `external/Trackdub` as a pinned, **read-only** git submodule (`.gitmodules`: `url = https://github.com/mesalimllc/Trackdub.git`, `branch = main`).

The command block below is quoted from `AGENTS.md` and is the canonical bump procedure; it is executed from the gated repo, not from here. The gated-side full procedure, including the desktop build/test gate, lives in `../Trackdub-gated/.opencode/context/processes/submodule-pin-bump.md`.

**Never edit files inside `external/Trackdub`.** A change belongs in this repo, gets merged, then gets pinned.

## The canonical command block

Verbatim from `Trackdub-gated/AGENTS.md`:

```bash
cd external/Trackdub && git fetch origin && git checkout <tag-or-sha> && cd ../.. && git add external/Trackdub && git commit -m "Bump core pin"
```

Breakdown:

| Step | Command | Notes |
|---|---|---|
| fetch | `git fetch origin` | inside `external/Trackdub`; brings tags and new commits |
| checkout | `git checkout <tag-or-sha>` | prefer an immutable tag or full SHA over a branch name |
| stage | `git add external/Trackdub` | from the gated repo root; stages only the submodule pointer |
| commit | `git commit -m "Bump core pin"` | always `-m`; the interactive editor is broken in this environment |

Pin inspection:

```bash
git submodule status                      # current pin + divergence
cd external/Trackdub && git log -1 --oneline && git describe --tags
```

## What lands in this repo instead

If the pin bump exposes a defect in the core, the fix is a normal core change:

1. Implement and validate here (`context/standards/validation-gates.md`).
2. Commit with an imperative title (`Add ...`, `Fix ...`, `Remove ...`), always `git commit -m`.
3. Push, open a PR, merge.
4. Only then bump the gated pin to the merged commit or its tag.

Never work around a core defect by editing the submodule working tree. The edit is invisible to this repo, disappears on the next `git submodule update`, and will not be in the pin.

## Breaking-change triage

Before moving the pin, check what the target commit changed relative to the current pin. In the gated repo:

```bash
cd external/Trackdub && git fetch origin
git log --oneline <current-sha>..<target-sha>
git diff --stat <current-sha>..<target-sha>
git diff <current-sha>..<target-sha> -- '*.csproj' 'Directory.Build.props' 'Directory.Packages.props' 'Directory.Packages.lock.json' '.github/workflows/*.yml'
```

| Area | What to check | Risk |
|---|---|---|
| Dependency graph | any `*.csproj` under `src/**` — a new or removed `ProjectReference` breaks `DependencyGraphTests` in the core and any consumer that mirrored the old shape | HIGH — a consumer referencing a project that no longer exists will not build |
| Package pins | `Directory.Packages.props`, any `packages.lock.json` | MEDIUM — restore graph changes, portable RID graphs can drop |
| Public API surface | contracts in `src/Trackdub.Contracts/`, SDK surface in `src/Trackdub.Sdk/`, CLI verbs in `src/Trackdub.Cli/Handlers/` | HIGH — the desktop shell binds to these |
| Stage names / ordering | `src/Trackdub.Domain/StageRuns/StageNames.cs`, `src/Trackdub.Application/Dubbing/DubbingPipelineStages.cs` | HIGH — a removed or renamed stage constant breaks the shell's pipeline UI and `StageFilter` handling |
| Readiness contract | `src/Trackdub.Contracts/Pipeline/ReadinessState.cs` | MEDIUM — new states are additive; removed/renamed ones are breaking |
| Manifest | `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json` | MEDIUM — a renamed `model_id` or `engine_family` invalidates saved selections and cached plans |
| Native layout | `src/Trackdub.OnnxRuntime.Dnnl.Native/`, `runtime/trt-rtx-ep.manifest.json`, `tools/dev/Fetch-*NativeDeps.ps1` | MEDIUM — native assets are not tracked; acquisition manifests are |
| Analyzer severity | `src/Trackdub.Analyzers/` (new diagnostic IDs or severity flips) | LOW — build warnings can become build errors under `-warnaserror` |
| CI workflows | `.github/workflows/ci.yml` | MEDIUM — a new gate means a local pass is not a CI pass |

## Validate both repos

**Core (`Trackdub`)** — at the target commit:

```bash
dotnet restore Trackdub.slnx -m:1
dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
dotnet test Trackdub.slnx --configuration Release --no-build -m:1
python tools/ci/verify-dependency-graph.py
dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1
```

**Consumer (`Trackdub-gated`)** — at the new pin:

```powershell
dotnet build Trackdub.slnx -m:1
dotnet test Trackdub.slnx -m:1
.\run.cmd    # UI-affecting changes only; a build is not proof the shell renders
```

Both green, or the pin does not move. If a consumer failure is caused by core, fix core; if it is caused by the consumer's own binding, fix the consumer and note it in the handoff.

## Recording the pin

The pin bump is externally visible and hard to reverse cleanly. Report, do not assume: tag or SHA pinned, old pin, commit range covered, breaking changes found, both validation results, and anything `NOT VERIFIED`. The bump commit goes through the normal PR lifecycle (`context/processes/pr-lifecycle.md`) unless the human explicitly asks for a direct commit.

## Related

- `context/processes/pr-lifecycle.md` — how the bump PR is opened and gated
- `context/standards/validation-gates.md` — the exact gate commands
- `context/domain/architecture.md` — cross-repo relationship and dependency graph
- `../Trackdub-gated/.opencode/navigation.md` — the desktop-side agent system
- `../Trackdub-gated/.opencode/context/processes/submodule-pin-bump.md` — the gated-side procedure