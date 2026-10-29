# Mitigate verified repository-wide security and quality findings

## Verified snapshot

Checked `Mesililm-LLC/Trackdub` `main` at `d63c60eb4a7693539feaa8dcfe2f5092431e96ae`.

- CodeQL: 3,256 open, 29 returned by fixed filter, 1 dismissed. All open instances reference current `main` SHA.
- Security-severity CodeQL: 30 open. 26 legitimate alert instances, 4 false positives.
- Quality CodeQL: 3,226 open severity-null recommendations, dominated by 1,964 `cs/path-combine` and 566 generic-catch alerts.
- Dependabot: 7 open, 64 fixed, 0 dismissed.
- Secret scanning: 0 open, 0 resolved.
- Security-related issues: [#430](https://github.com/Mesililm-LLC/Trackdub/issues/430) remains open. Linked old alert is fixed, but equivalent current alert #20 survives.

## Legitimate open findings

### Security CodeQL

- High, `actions/untrusted-checkout/high`: privileged Tessl job executes same-repository PR code after using secrets and with `pull-requests: write`. `.github/workflows/tessl-trackdub-review.yml:82-90`, [alert 20](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/20), open.
- Medium, `actions/missing-workflow-permissions`: release jobs inherit repository defaults at `.github/workflows/release.yml:13,36,67,103`, [5](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/5), [6](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/6), [8](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/8), [9](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/9); TRT smoke inherits defaults at `.github/workflows/trt-rtx-smoke.yml:12`, [7](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/7). All open.
- Medium, `cs/log-forging`: request path/method and exception messages can contain line breaks under simple console logging. `ExceptionHandlerMiddleware.cs:77,82`, [15](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/15), [16](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/16), [17](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/17), [18](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/18). Open.
- Medium, `actions/unpinned-tag`: 16 movable third-party action references. Open alerts:
  - `api-deploy.yml:30,37,40,43,85,92`: [21](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/21), [3230](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3230), [3231](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3231), [3160](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3160), [26](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/26), [27](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/27)
  - `ci.yml:27`: [25](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/25)
  - `code-coverage.yml:74,86`: [28](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/28), [3161](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3161)
  - `frontend-build.yml:18`: [31](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/31)
  - `model-audit.yml:18`: [32](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/32)
  - `opencode.yml:40`, `opencode-review.yml:61`: [33](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/33), [34](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/34)
  - `release.yml:111`: [35](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/35)
  - `tessl-trackdub-review.yml:91`: [36](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/36)
  - `dependabot-auto-merge.yml:19`: [3159](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3159)

### Dependabot

GitHub supplies manifest, not source line. Current lock lines added from `main`.

- Low, `GHSA-866g-f22w-33x8`: `@ai-sdk/provider-utils` 3.0.25, `package-lock.json:119`, [alert 48](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/48). Dev-tool response resource consumption; no patched release.
- High, `GHSA-hmw2-7cc7-3qxx`: `form-data` 4.0.5, `package-lock.json:937`, [alert 49](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/49). Patched in 4.0.6.
- Hono 4.12.23, `package-lock.json:1099`, patched in 4.12.25:
  - Medium `GHSA-j6c9-x7qj-28xf`, [50](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/50)
  - Medium `GHSA-wwfh-h76j-fc44`, [51](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/51)
  - High `GHSA-88fw-hqm2-52qc`, [52](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/52)
  - Medium `GHSA-wgpf-jwqj-8h8p`, [53](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/53)
  - Medium `GHSA-rv63-4mwf-qqc2`, [54](https://github.com/Mesililm-LLC/Trackdub/security/dependabot/54)

Hono and `form-data` vulnerable functions are not reachable from shipped Trackdub code. They enter through unreferenced root developer packages. Alerts remain legitimate dependency-hygiene findings, not product exploit paths.

### Confirmed quality defects

- `cs/local-not-disposed`: undisposed ONNX `RunOptions`, `LanguageModel.cs:209`, [65](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/65).
- `cs/loss-of-precision`: integer multiplication may overflow before conversion, `CosyVoiceLengthRegulator.cs:136`, [2976](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/2976).
- `cs/cast-from-abstract-to-concrete-collection`: brittle `IReadOnlyList` to `List` cast, `RuntimePlanner.cs:403`, [280](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/280).
- `cs/dispose-not-called-on-throw`: first backend disposal can prevent second cleanup, `PlaybackAbstractions.cs:520-521`, [52](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/52), [53](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/53).
- `cs/dispose-not-called-on-throw`: redundant `Close` can bypass `Dispose`, `ProjectLock.cs:137`, [54](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/54).

## False positives and historical state

- False positive, `actions/untrusted-checkout-toctou/high`: checkout uses immutable resolved `headRefOid`, so no ref TOCTOU. [Alert 19](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/19).
- Intended fixed-boundary review publishing, not arbitrary exfiltration/write: [alerts 45](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/45) and [46](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/46).
- Generated MSW service worker validates controlled client ID; browser scope supplies origin boundary. `mockServiceWorker.js:23`, [alert 47](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/47).
- Quality false positives include localized runtime format strings [63](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/63), conditionally formatted fixed model paths [64](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/64), explicit session ownership transfer [49](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/49)-[51](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/51), and intentional process-global TRT registration [2977](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/2977).
- Fixed CodeQL security findings: cache poisoning [2](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/2), prior privileged checkouts [3](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/3), [4](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/4), prior action pins [22](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/22)-[24](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/24), [29](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/29), [30](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/30).
- Fixed CodeQL quality findings: 11 path-combine, 8 generic-catch, 1 LINQ recommendation. Remaining fixed-filter result overlaps dismissed alert.
- Dismissed: medium `actions/missing-workflow-permissions`, `.github/workflows/windows-build.yml:11`, false positive, [alert 1](https://github.com/Mesililm-LLC/Trackdub/security/code-scanning/1).
- Dependabot fixed: 64 alerts across root `package-lock.json`, frontend lock, cursor-tool lock, and API project. No dismissed alerts.
- Secret scanning: no current or historical findings.

## Implementation changes

- Split Tessl workflow into unprivileged analysis job and trusted publishing job. PR code receives no secrets or write token; publishing job consumes data artifact and runs only script checked out from trusted workflow SHA.
- Add top-level `contents: read`; grant `contents: write` only to release-publishing job. Keep TRT workflow read-only.
- Pin every external action to current immutable SHA, retaining version comments. Include Graphite `main`, OpenCode `latest`, Tessl, AWS, Docker, coverage, release, and Dependabot actions.
- Normalize CR/LF in attacker-influenced log fields before structured logging; keep original exception object for stack trace.
- Remove unreferenced root `@supermemory/tools` and `@modelcontextprotocol/server-everything` dependencies. Regenerate `package-lock.json` and `bun.lock`, closing all seven open Dependabot alerts without shipping unused developer servers.
- Fix five confirmed quality roots: dispose `RunOptions`; cast before multiplication; use `List<DeviceEntry>` dictionary values; guarantee both playback backends are cleaned independently; replace `Close` plus `Dispose` with one reliable disposal path.
- Change canonical CodeQL workflow from redundant `security-extended,security-and-quality` to `security-extended`. Keep compiler warnings-as-errors, format verification, analyzers, and tests as quality gates. This removes security-dashboard landfill while preserving broader security queries.
- Dismiss four verified security false positives with evidence comments. Update issue #430 to current alert #20, then close after workflow rerun proves resolution.

## Interfaces and tests

- No public application API or schema changes.
- Add unit tests for CR/LF log normalization, large CosyVoice interpolation dimensions, ONNX `RunOptions` disposal, backend cleanup when first disposal throws, and project-lock cleanup.
- Validate workflow YAML, permission boundaries, immutable action references, fork/same-repository PR behavior, release publishing, and Tessl artifact handoff.
- Run focused inference, media-playback, SDK, API, and workflow checks; then full Release build/test.
- Acceptance: current high/medium legitimate CodeQL alerts close, seven Dependabot alerts close, secret scanning stays empty, false positives carry documented dismissals, issue #430 closes, and no new security alert replaces fixed instances.
