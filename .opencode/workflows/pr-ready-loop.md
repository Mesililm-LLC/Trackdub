# PR Ready Loop

Drives a change from a dirty working tree to a merge-ready pull request, with every claim backed by a command that actually ran. The loop is deliberately sequential: local validation is cheap and runs first, the Release warnings-as-errors gate runs before anything is published, and no commit is created until the gate is green. The governing rule is that a red PR is never cheaper than a false one — if a gate fails, the loop stops.

## When to Use This Workflow

Use when:
- A change exists in the working tree or on a branch and must become mergeable.
- Review threads need triage and a fix loop.
- A PR's CI checks must be watched to completion.
- A fix must land on a base branch before the dependent change can merge (stacked PRs).

Do NOT use for:
- Adding a pipeline stage → `new-inference-stage.md` first, then this workflow.
- A performance regression → `benchmark-regression-triage.md`.
- A native dependency bump → `/pin` via `.opencode/context/processes/submodule-pin-bump.md`.
- Reading-only question about the codebase. Do not open a PR.

## Inputs

| Input | Required | Description | Default |
|---|---|---|---|
| Target branch | yes | Branch the change lands on, usually `main` | `main` |
| Scope | yes | What is in this change, in one sentence | none |
| External issue ref | no | Only if the user explicitly provides one | none |
| Base of stack | if stacked | Branch that must merge first | none |
| Push authorization | yes | Human approval to push | required at Stage 7 |

## Preconditions

- `gh` is installed and authenticated. Verify with `gh auth status`.
- The repo is `mesalimllc/Trackdub` on branch `main`.
- `dotnet` SDK matches `global.json`.
- For stacked work, `gh stack` is available. Verify with `gh stack --help`.
- The change does not contain a hand-resolved `packages.lock.json` conflict.

## Execution Stages

### Stage 1 - Working Tree Audit

- **Goal**: Know exactly what will be committed before committing anything.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`
- **Actions**:
  1. List branch, status, and diff. Use the GitKraken tools for workspace-aware git operations (`gitkraken_workspace_list`, `git_branch`, `git_log_or_diff`).
  2. Inventory every changed file by project. Flag changes that add a `ProjectReference` — those require an AGENTS.md diagram edit or Architecture tests will fail.
  3. Flag any `packages.lock.json` in the diff. Do not hand-merge it; take one side and run `dotnet restore Trackdub.slnx --force-evaluate -m:1`.
  4. Flag build output, secrets, absolute local paths, and machine-specific paths in the diff.
  5. Confirm the change compiles before any polish work.
- **Exit criteria**: Exact file list known; nothing unbuildable or unsafe staged for commit.
- **Failure handling**: Unresolved conflict or junk in the tree → resolve here, before spending validation time.

### Stage 2 - Dev-Loop Validation (fast)

- **Goal**: Catch cheap failures before the expensive gate.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/standards/validation-gates.md`, `.opencode/context/standards/coding-standards.md`
- **Actions**:
  1. ```bash
     dotnet build Trackdub.slnx -m:1
     dotnet test Trackdub.slnx -m:1
     ```
  2. For a focused failure, narrow with `dotnet test tests/Trackdub.<Area>.Tests --no-restore -m:1` or `dotnet test tests/Trackdub.Application.Tests --filter "FullyQualifiedName~<TestName>" -m:1`.
  3. Reject new `Path.Combine` occurrences in changed lines (`BannedSymbols.txt`). This is currently a warning, not an error — catch it here rather than letting it accumulate.
- **Exit criteria**: Debug build clean; full Debug test suite green.
- **Failure handling**: Red here means red later. Fix before advancing. Route deep diagnosis to `core-diagnostics`.

### Stage 3 - Full Release CI Gate

- **Goal**: Reproduce CI exactly. This is the gate that authorizes publishing.
- **Actor**: subagent `validation-gate`
- **Context to load**: `.opencode/context/standards/validation-gates.md`, `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. ```bash
     dotnet restore Trackdub.slnx -m:1
     dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror
     dotnet test Trackdub.slnx --configuration Release --no-build -m:1
     ```
  2. Confirm Architecture tests passed inside that run. They enforce Domain-has-no-references, Contracts-references-only-Domain, the AGENTS.md diagram match, `StageNames` consistency, and the no-base-branch-filter guard on `ci.yml`, `codeql.yml`, `model-audit.yml`, `benchmark-report-validation.yml`.
  3. Treat any warning under `-warnaserror` as a hard failure. Do not suppress to make it pass.
- **Exit criteria**: Release build with warnings-as-errors green; full Release test suite green.
- **Failure handling**: FAIL → `core-diagnostics`, fix, re-run this stage. **Stop condition: do not push a red PR to save time.** Only an explicit human instruction overrides this.

### Stage 4 - Architecture Bounds Audit

- **Goal**: Confirm the change respects layer direction.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/domain/architecture.md`, `.opencode/context/standards/architecture-rules.md`
- **Actions**:
  1. Domain depends on nothing. No new reference out of `Trackdub.Domain`.
  2. No inference leaking upward into Application, Contracts, or Media.
  3. AGENTS.md diagram matches every `src/**.csproj` `ProjectReference` in both directions.
  4. Confirm with `dotnet test tests/Trackdub.Architecture.Tests --no-restore -m:1`.
- **Exit criteria**: No upward or sideways edge introduced.
- **Failure handling**: Violation → `core-diagnostics`; invert with an interface rather than adding a reference.

### Stage 5 - Pre-PR Self-Review (read-only)

- **Goal**: Catch defects before a human reviewer spends attention.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`
- **Actions**:
  1. Review the **local diff** only — `git diff "$BASE_REF"...HEAD` plus `git diff HEAD`. Nothing here is PR-scoped yet, because the PR does not exist until Stage 8.
  2. Triage each finding as real-and-worth-fixing, not-real, or intended-behavior. Fix what is real now, before the commit and push.
  3. Read-only in judgement: never dismiss a finding to make the change look clean.
  4. Record the finding list in the working notes so Stage 10 can compare against it.
- **Exit criteria**: Every finding has a decision; real ones are fixed and re-validated.
- **Failure handling**: If a finding cannot be adjudicated without more information, carry it forward to Stage 10 rather than guessing.

> **cubic runs in Stage 10, not here.** `trigger_pr_review` and `get_pr_issues` are PR-scoped: they need a PR number. Triggering a PR review before the PR exists cannot work, so this stage deliberately uses local-diff review only. Cubic review belongs after Stage 8 and is re-run on every fix round in Stage 10.

### Stage 6 - Commit

- **Goal**: Create a reviewable commit. **Always `git commit -m`.**
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/standards/coding-standards.md`
- **Actions**:
  1. Stage only intended paths. Use GitKraken `git_add` with explicit pathspecs, not `git add -A`.
  2. Imperative title: `Add ...`, `Fix ...`, `Remove ...`.
  3. **Always use `git commit -m "<message>"`. The interactive commit editor is broken in this environment — never open it.** Use GitKraken `git_commit` (`git commit -m <message> [files...]`).
  4. One logical change per commit. Split unrelated work.
- **Exit criteria**: Commit exists; working tree holds only intended changes.
- **Failure handling**: Accidental staging → unstage and restage explicitly.

### Stage 7 - Push

- **Goal**: Publish the branch.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`
- **Actions**:
  1. Confirm Stage 3 was green immediately before this stage. If time has passed or the tree changed, re-run the gate.
  2. Push with the GitKraken `git_push` tool for workspace-aware behavior. Verify remote and branch first with `gitkraken_workspace_list` and `git_branch`.
  3. For stacked branches, confirm the base branch is pushed first; GitHub needs the base to exist to compute the diff.
- **Exit criteria**: Branch present on the remote at the expected commit.
- **Failure handling**: Push rejected ⇒ the base branch is missing or history diverged. Fix the base first; never force-push over a shared branch.

### Stage 8 - Open or Update the PR

- **Goal**: Give reviewers a coherent diff and the evidence.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`, `.opencode/context/templates/evidence-report.md`
- **Actions**:
  1. Open or update with `gh pr create` / `gh pr edit`.
  2. Body states: what changed, why, the exact commands run with results, and what was deliberately not changed.
  3. Reference an issue ID only if the user provided one. Use an external tracker only when the user explicitly asks; otherwise track work in Git and create persisted plans only when requested.
  4. Keep the PR scope to one logical change.
- **Exit criteria**: PR open against the correct base; body carries the gate evidence.
- **Failure handling**: Wrong base ⇒ recreate against the intended base.

### Stage 9 - CI Check Polling

- **Goal**: Know the real CI verdict from CI, not from assumption.
- **Actor**: `trackdub-orchestrator`
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`
- **Actions**:
  1. Watch with `gh pr checks <pr-number>` until all checks reach a terminal state.
  2. Relevant workflows: `ci.yml`, `codeql.yml`, `code-coverage.yml`, `model-audit.yml`, `release-shipping-guard.yml`, `opencode-review.yml`, `trt-rtx-smoke.yml`.
  3. Benchmarks are **not** part of this gate. BenchmarkDotNet does not run on pull-request CI by policy; real-model and saved-commit comparisons are opt-in via `benchmark-dotnet.yml`. Never wait on a benchmark check here.
  4. If `model-audit.yml` fails, the change touched model inventory or manifest integrity — route to `benchmark-regression-triage.md` scope or `core-diagnostics`.
- **Exit criteria**: All required checks terminal and green.
- **Failure handling**: Any red check → capture the exact failing log, fix, return to Stage 2. Do not merge around a red check.

### Stage 10 - Review Thread Triage and Fix Loop

- **Goal**: Resolve every open thread with a decision, not silence.
- **Actor**: `trackdub-orchestrator`; fixes by the relevant subagent
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`, `.opencode/context/standards/coding-standards.md`
- **Actions**:
  1. Read open threads: `gh pr view --comments` for human review, and cubic `get_pr_issues` for bot findings.
  2. Trigger a cubic review if one has not run since the last push: `trigger_pr_review` once — it returns immediately without progress, so do not poll it — then read with `get_pr_issues`. An empty array means no open issues remain. (cubic CLI is not installed here, verified `cubic --version` → not found; the MCP tools are the route. If neither is available, record the gap rather than claiming a clean review.)
  3. Triage each: fix, or reply with the reason it is intended behavior. Never dismiss a thread to make the PR look clean.
  4. Cubic threads: `update_pr_issue_status` with `resolved` after the fix lands, or `false_positive` / `wont_fix` / `intended_behavior` with a comment when dismissing. Only `get_pr_issues` IDs are valid there — never a codebase-scan issue ID.
  5. For each fix round: re-run Stage 2, then Stage 3, then push, re-watch Stage 9, and re-trigger the cubic review in step 2. A review that is never re-triggered will not see the fix.
  6. If a fix must land on the base before this PR, use `gh stack` to model the dependency. `WorkflowTriggerTests` guarantees `pull_request` triggers are not narrowed to a base branch, so stacked PRs do run CI.
- **Exit criteria**: Zero open threads; each resolved by fix or documented intent.
- **Failure handling**: Disagreement with a reviewer → escalate to human. Do not self-approve.

### Stage 11 - Merge

- **Goal**: Land the change with its evidence intact.
- **Actor**: human
- **Context to load**: `.opencode/context/processes/pr-lifecycle.md`
- **Actions**:
  1. Confirm: gate green, CI green, threads resolved, branch up to date with its base.
  2. Merge. Re-run Stage 3 locally if the base moved during review.
  3. Confirm the change landed on `main` and post-merge CI is green.
  4. **External issue trackers are opt-in.** Update one only on explicit user request. Never claim Done without proof: the proof is the green post-merge run, recorded in the handoff.
- **Exit criteria**: Change on `main`; post-merge CI green.
- **Failure handling**: Post-merge red ⇒ revert promptly and diagnose.

## Decision Points

| Condition | Decision |
|---|---|
| Stage 3 gate red | Stop. Do not push. Only an explicit human instruction overrides |
| Architecture bounds violated | Invert with an interface; do not add a reference |
| `packages.lock.json` conflicts | Take one side, then `dotnet restore Trackdub.slnx --force-evaluate -m:1` |
| Fix must precede the main change | Stack with `gh stack`; base PR first |
| Cubic finding is real | Fix in the same PR, re-run Stage 2 and 3 |
| Cubic finding is not real | Reply with reasoning; mark `false_positive` / `intended_behavior` |
| Checks pending after push | Wait. Never merge around a pending check |
| Only benchmarks are red | Out of scope for this gate — `benchmark-regression-triage.md` |
| Human wants an external tracker updated | Only on explicit request, with attached proof |

## Gates

- **G1 — Fast gate** (end Stage 2): Debug build + full Debug tests green.
- **G2 — Release gate** (end Stage 3): `dotnet restore`, Release `-warnaserror` build, full Release tests. Authorizes publishing.
- **G3 — Bounds gate** (end Stage 4): Domain-has-no-references, Contracts-only-Domain, diagram match.
- **G4 — Remote gate** (end Stage 9): every required `gh pr checks` terminal and green.
- **G5 — Thread gate** (end Stage 10): zero open threads, each resolved by fix or documented intent.
- **Stop condition**: A failed gate halts the loop. Do not push a red PR to save time unless the human explicitly instructs it.

## Failure Modes

| Symptom | Likely cause | Recovery |
|---|---|---|
| Commit opens an editor and hangs | Interactive editor invoked | Use `git commit -m`; the editor is broken here |
| Push rejected, remote ref missing | Base branch not pushed | Push the base first |
| `-warnaserror` fails on a new warning | New warning introduced, or suppressed | Fix the warning; do not suppress |
| `Packages.lock.json` merge conflict | Hand-resolved conflict | `git checkout --ours/--theirs`, then `--force-evaluate` |
| Architecture test: csproj not in diagram | New `ProjectReference` | Update the AGENTS.md diagram |
| Architecture test: diagram lists missing project | Stale diagram entry | Remove it; test checks both directions |
| Stacked PR never runs CI | Someone added a `branches:` filter | `WorkflowTriggerTests` blocks this; re-run Architecture tests |
| Cubic review never returns | `trigger_pr_review` does not report progress | Do not poll; read with `get_pr_issues` later |
| Model-audit check red | Model manifest or inventory changed | `core-diagnostics`; verify manifest and license metadata |
| Post-merge CI red | Base moved or merge artifact | Revert promptly, diagnose on the reverted state |
| `/ready` disagrees with local green | Non-deterministic or hardware-dependent test | Capture both results; treat divergence as a real finding |

## Evidence to Collect

Per `.opencode/context/templates/evidence-report.md`:
- Working-tree file list before and after commit.
- Verbatim commands and pass/fail for Debug build, Debug tests, restore, Release `-warnaserror` build, Release tests.
- Architecture test output.
- PR URL, base branch, commit sha.
- `gh pr checks` terminal output with each check name and state.
- Every review thread with its triage decision and the code change that resolved it.
- Confirmation that no external issue tracker was touched, or the explicit user request that authorized an update.

## Completion Checklist

- [ ] Working-tree audit done; no conflict or junk staged.
- [ ] `dotnet build Trackdub.slnx -m:1` green.
- [ ] `dotnet test Trackdub.slnx -m:1` green.
- [ ] `dotnet restore Trackdub.slnx -m:1` green.
- [ ] `dotnet build Trackdub.slnx --configuration Release --no-restore -m:1 -warnaserror` green.
- [ ] `dotnet test Trackdub.slnx --configuration Release --no-build -m:1` green.
- [ ] Architecture tests green; no new project references without diagram updates.
- [ ] No new `Path.Combine` in changed lines.
- [ ] Committed with `git commit -m` and an imperative title.
- [ ] Pushed only after the Release gate was green.
- [ ] PR opened/updated against the correct base with evidence in the body.
- [ ] All `gh pr checks` terminal and green; no benchmark checks awaited.
- [ ] Every review thread resolved by fix or documented intent.
- [ ] Stacked work modeled with `gh stack` where a base fix was required.
- [ ] Merged; post-merge CI green.
- [ ] No external issue tracker touched by the agent; no Done without proof.
