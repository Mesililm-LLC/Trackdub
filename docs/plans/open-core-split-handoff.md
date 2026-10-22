# Agent Handoff: Trackdub Repository Split

Use this handoff to continue the Trackdub open-core split. Treat `docs/plans/open-core-split-continuation.md` as the canonical execution plan.

## Mission

Finish the public Apache-2.0 core release gates, then rebuild the private desktop product repository with fresh history and a pinned dependency on the public core.

Do not treat the current state as complete. The core is structurally separated but still private and not green in remote CI. The existing `Trackdub-gated` repository is source staging, not the final private product repository.

## Current Repositories

| Repository | Current role | Rule |
|---|---|---|
| `mesalimllc/Trackdub` | Private public-core candidate | Work here first; do not make public until gates pass |
| `mesalimllc/Trackdub-gated` | Private staging lane | Read-only source until the fresh-root rebuild phase |
| `mesalimllc/Trackdub-Monorepo-Archive` | Private archived history | Never modify |
| `mesalimllc/api.trackdub` | Private future server activation/API | Do not change during Phase 1 |
| `mesalimllc/portal.trackdub` | Private portal | Out of scope |
| `mesalimllc/trackdub.com` | Private marketing site | Out of scope |

## Facts Already Verified

- `Trackdub` is private, default branch `main`, with fresh-root history beginning at `8bac38a8fa3e0343c5d10c558b64d269d15e7828`.
- The latest inspected core commit is `d590533b3220edf5211ce773b7620487c29eb1e7`.
- The core uses `Trackdub.slnx`; do not reintroduce legacy `.sln` or `.slnf` assumptions.
- The public core has the canonical Apache-2.0 `LICENSE` and root `NOTICE`.
- `IExportTierGate` remains in public Contracts. The public core must not register a concrete product policy.
- Desktop watermark, duration limit, Free/Pro mapping, activation client, packaging, and release logic belong only in the final private desktop repository.
- The legacy activation service is not a desktop dependency. `api.trackdub` will eventually replace it on the server side.

## Known Remaining Core Defects

Do not rediscover these from scratch; verify and then address them.

1. The latest remote CI run failed.
   - macOS build/test and formatting passed.
   - Windows failed at `WavePcm16Tests.WriteSamplesAsync_move_failure_deletes_temp_file_and_preserves_destination`: the test expects exactly `Exception`, but Windows reported `UnauthorizedAccessException`.
   - Linux needs a fresh, inspectable rerun before it can be declared healthy.
2. CodeQL fails.
   - GitHub Advanced Security is not enabled for this private repository.
   - `.github/codeql/codeql-config.yml` still includes the removed `frontend` path.
3. Dependabot auto-merge merged major actions updates despite a patch/minor-only policy. The policy needs evidence-based repair.
4. Core documentation still has monorepo-era contradictions:
   - `docs/legal/CONTRIBUTOR-LICENSE-AGREEMENT.md` describes GPLv3 plus commercial dual licensing.
   - `docs/legal/THIRD_PARTY_NOTICES.md` contains desktop/App.Avalonia/LibVLC runtime claims.
   - `IExportTierGate` commentary says Application implements it.
   - `Directory.Build.props` lacks explicit opt-in package ownership/license metadata.

## Operating Rules

- Read `AGENTS.md`, `docs/repository-policy.md`, and the canonical plan before editing.
- Start from current `Trackdub/main` on a normal topic branch.
- Do not rewrite history, force-push, reset remote history, make the core public, or touch the archive.
- Do not modify `Trackdub-gated`, `api.trackdub`, `portal.trackdub`, or `trackdub.com` during the first task.
- Do not weaken tests merely to turn a failure green. Establish the behavior contract first.
- Do not add a no-op export gate to public core unless compilation proves it is necessary; report the dependency instead.
- Do not add per-file SPDX headers.
- Do not track native binary payloads, downloaded models, caches, build outputs, or machine-specific agent state.
- Do not copy cloud, activation-server, portal, website, Docker, or infrastructure code into the core or final desktop repository.
- Use `AGENTS.md` as the only substantive tracked agent-instruction file. Tool-specific prompt libraries and state stay untracked.

## Authorized First Task: Phase 1 Only

Complete the public-core release-readiness work described in Phase 1 of the canonical plan.

### Required work

1. Establish a green CI baseline across Windows, Linux, and macOS.
2. Resolve the Windows Media test using the intended contract, not a broad assertion.
3. Repair CodeQL configuration for the core-only tree and report the GitHub Advanced Security decision needed to make the gate enforceable.
4. Repair Dependabot auto-merge governance so major updates cannot merge automatically and checks are genuinely required.
5. Remove stale GPL/dual-license contribution wording and replace it with Apache-2.0 contribution guidance.
6. Separate public-core third-party notices from desktop packaging notices.
7. Correct `IExportTierGate` documentation and add package metadata only through explicit opt-in for Trackdub-owned packages.
8. Add or finish repository-boundary and stale-claim checks.
9. Run full local and remote validation.

### Stop conditions

Stop and ask for owner direction if any of these becomes necessary:

- Enabling GitHub Advanced Security or another paid GitHub security feature.
- Choosing an approved consumer EULA.
- Publishing packages to NuGet.org or GitHub Packages.
- Making `Trackdub` public.
- Recreating or renaming `Trackdub-gated`.
- Any deletion or move that would discard the only retained copy of source material.
- Any uncertainty about third-party redistribution rights or model licensing.

## Required Phase 1 Report

Before proposing a merge, report:

- Current core branch, base SHA, and proposed head SHA.
- Exact test behavior and the evidence behind the Media test fix.
- CI run URLs/IDs and job conclusions for Windows, Linux, macOS, format, and CodeQL.
- CodeQL scope after removing stale monorepo assumptions.
- The exact GHAS/publication decision still needed, if any.
- Dependabot policy changes and confirmation that major updates are blocked from auto-merge.
- Every deleted, moved, or rewritten legal/attribution document.
- Every packable project and its final ownership/license metadata.
- Results of repository-boundary, stale-license, secret, private-URL, native-payload, and generated-file scans.
- Any remaining blocker.

End with exactly one of:

```text
Phase 1 ready for owner review: Yes
```

or:

```text
Phase 1 ready for owner review: No
Blocker: <specific decision or failed verification>
```

## Next Phases After Approval

After explicit owner approval of Phase 1:

1. Preserve the current gated staging repository privately and recreate `Trackdub-gated` with a fresh root.
2. Build the final desktop repository from an exact file inventory.
3. Pin `external/Trackdub` to the verified core SHA via Git submodule and project references.
4. Move desktop/product policy into one concrete desktop export gate with desktop-owned tests and test doubles.
5. Rebuild packaging, legal artifacts, and production EULA release checks.
6. Validate clean clones and make the core public only after every agreed release gate passes.
7. Port activation-server behavior into `api.trackdub` separately; it does not block the repository split.
