# ADR-0012: WavePcm16 loudness policy — per-call-site opt-in + Roslyn analyzer

- Status: Accepted
- Date: 2026-07-23

## Context

`WavePcm16.WriteSamplesAsync` defaults `normalizePeak: false` to preserve
historical per-sample hard-clip semantics on cumulative mixes. The 5-arg
overload forwards to the 6-arg overload with `normalizePeak: false`; the
6-arg overload's bool flag is opt-in.

A recent bug in `PreviewRangeRenderer` — the only production caller writing
post-multichannel-downmix output — revealed that hot 5.1 mixes silently
hard-clipped to `short.MaxValue`. The bug was detected because a regression
test pinned the expected post-scale values (L ~1.0 / R ~0.686). Without
that test the bug would have shipped silently. The fix added
`normalizePeak: true` to the call site without touching the writer default.

Inventory of `WavePcm16.WriteSamplesAsync` / `WriteMonoAsync` call sites in
`src/` (per the wave-pcm16 hygiene grep):

- `src/Trackdub.Media/Mixing/PreviewRangeRenderer.cs:171` — **HIGH risk**
  (multichannel→stereo additive downmix; cumulative peaks reach ~2.06).
  Opted in (`normalizePeak: true`) this lane (commit `912ae905`).
- `src/Trackdub.Media/Tts/TtsAudioPostProcessor.cs:47` — **NARROW risk**
  (single mono TTS engine output; bounded by the TTS model). No change.
- `src/Trackdub.Media/Waveforms/Pcm16ReferenceClipTrimmer.cs:87` —
  **LOW-MEDIUM risk** (silence-trim pass-through; doesn't additively mix).
  Not migrated this lane.
- `src/Trackdub.Media/Stretch/WsolaPhonemeStretchService.cs:119` —
  **LOW-MEDIUM risk** (WSOLA overlap-add; can theoretically exceed |1| in
  pathological cases). Not migrated this lane.

The bug class — silent hard-clip on hot cumulative output — is reproducible
by any future caller that:

1. mixes samples from multiple sources; OR
2. applies a transform whose output can exceed the input's dynamic range; AND
3. forgets to opt in to `normalizePeak: true`.

Today nothing in the build pipeline warns such callers.

## Decision

Adopt a **per-call-site opt-in** convention backed by a
`WavePcm16MultiSourceMixOptIn` Roslyn analyzer that detects when a caller
inside a method whose name contains Mix, Mixer, Blend, or Render calls
`WavePcm16.WriteSamplesAsync` without `normalizePeak: true`.
`PreviewRangeRenderer.cs:171` sets the precedent for the opt-in shape.
The analyzer recognizes only `WriteSamplesAsync` (which has the `normalizePeak`
parameter); `WriteMonoAsync` is excluded because it cannot resolve the finding.

**Convention:**

- Every caller buffering multi-source or post-transform mixed output that
  can exceed |1| MUST pass `normalizePeak: true`.
- Callers writing single-source PCM within a known bounded range (mono TTS,
  silence-trim pass-through) MAY pass `normalizePeak: false`.
- The default in `WavePcm16.WriteSamplesAsync` stays `false` to preserve
  historical loudness for in-range inputs.

**Deferral — explicit non-migration this lane:**

`Pcm16ReferenceClipTrimmer` (LOW-MEDIUM) and `WsolaPhonemeStretchService`
(LOW-MEDIUM) **are not migrated in this lane**. Rationale (per AGENTS.md
"Touch waves, not ripples"):

- Both are single-source transformations, not multi-source additive mixes.
- Their input loudness is bounded by upstream single-source pipelines.
- Loudness surprise for any caller downstream (final mix export, dub
  playback) is a UX decision, not a correctness fix; needs a separate
  loudness-policy ADR that covers the loudness-vs-final-mix tradeoff (see
  "Future remediation" below).
- This lane pinned the convention without flipping any defaults outside
  `PreviewRangeRenderer.cs`.

**Future remediation:** A future lane (after the §4.4 / C8 / §9.1 /
log-rotation-fix stack has merged and stabilised) should re-audit the 2
LOW-MEDIUM callers with empirical WSOLA-peak and silence-trim-passthrough
data before opting them in. The Roslyn analyzer should automatically flag
them if a new multi-source mix path appears in their input graph.

## Consequences

**Positive:**

- The bug class is caught at compile time for new code paths via the
  analyzer, replacing the current "test must exist" discovery mechanism.
- The 22/22 `PreviewRangeRenderer` suite + 9/9 `WavePcm16` suite + 155/0/3
  full `Trackdub.Media.Tests` confirm the opt-in path is safe and does not
  regress in-range sources.
- No loudness surprise for callers currently in production — the default
  stays `false`; only the 1 already-fixed production caller changed.
- The convention is small, reviewable, and survives future feature work.

**Negative:**

- The 2 LOW-MEDIUM callers remain on the un-opted default; a pathological
  WSOLA overlap or upstream hot-trim pass-through could still hard-clip
  silently.
- The Roslyn analyzer adds a new tooling surface (rule, fixtures, CI
  integration) to maintain.
- Per-call-site opt-in is review-dependent; reviewers must know the
  convention or the analyzer must catch the slip.
- This ADR fixes only the discovery mechanism. A follow-up loudness-policy
  ADR must decide whether to flip the default, and that ADR must migrate
  the 2 deferred sites if it does.

## Alternatives considered

### Flip the default (`normalizePeak: true` from overload 1) and require explicit opt-out

Rejected for this milestone:

- Every existing `WavePcm16.WriteSamplesAsync` caller would suddenly emit
  quieter output (loudness surprise), violating AGENTS.md "Touch waves, not
  ripples" until data justifies a global loudness change.
- The 4 scaler facts in `WavePcm16Tests.cs` + the hot-5.1 test pin
  specific PCM short values; the entire `Trackdub.Media.Tests` suite would
  need updating to reflect new post-scale amplitudes.
- Strongest "policy-as-default" answer but premature without empirical
  loudness impact data across all known callers.

### Introduce `WavePcm16LoudnessPolicy` enum (`Preserve` | `Normalize` | `ForceLimit`) replacing the bool

Rejected for this milestone:

- Three call-site migrations (only `PreviewRangeRenderer` opted in this
  lane; the 2 LOW-MEDIUM callers are deferred) plus tests plus contract
  docs.
- Adds runtime-introspection code paths (`Preserve` vs `ForceLimit`) the
  codebase has no immediate use for.
- Premature abstraction; defer until a second concrete policy requirement
  surfaces (peer review, dub playback loudness target, etc.).

### Keep opt-in without an analyzer

Rejected: the bug class was discovered only because a test existed.
Without the test, silent hard-clip would have shipped. Code review alone
is insufficient to catch future omissions; the analyzer is the safety net.

## References

- `src/Trackdub.Media/Waveforms/WavePcm16.cs` — the writer; overloads
  surfaced at L282–305; scaler at L370–395.
- `src/Trackdub.Media/Mixing/PreviewRangeRenderer.cs:171` — the opt-in
  precedent (`normalizePeak: true` at the `WavePcm16.WriteSamplesAsync`
  call site inside `RenderAsync`).
- `src/Trackdub.Media/Waveforms/Pcm16ReferenceClipTrimmer.cs:87` —
  LOW-MEDIUM caller, **deferred** this lane.
- `src/Trackdub.Media/Stretch/WsolaPhonemeStretchService.cs:119` —
  LOW-MEDIUM caller, **deferred** this lane.
- `src/Trackdub.Media/Tts/TtsAudioPostProcessor.cs:47` — NARROW caller,
  no change.
- `tests/Trackdub.Media.Tests/PreviewRangeRendererTests.cs` (~L1066) —
  `RenderAsync_writes_hot_five_one_pcm16_at_unit_peak_via_per_track_normalization`
  regression test that pinned the bug.
- `tests/Trackdub.Media.Tests/WavePcm16Tests.cs` (~L100–200) — four new
  scaler facts: overflowing, in-range, NaN/Inf, exact-source.
- `docs/adr/ADR-0011-contracts-domain-coupling.md` — style template
  (YAML bullet-list frontmatter + sectioned prose).
- `docs/adr/README.md` — directory purpose + ADR conventions.
- Commit `685776f3` — wave-pcm16 cherry-pick (3 files: writer + 2 test files).
- Commit `912ae905` — call-site opt-in fix (1 file: `PreviewRangeRenderer.cs`).
- PR #535 — `https://github.com/Mesililm-LLC/Trackdub/pull/535`
  (branch `chore/wave-pcm16-normalization`, base `main`).
- `AGENTS.md` — "Touch waves, not ripples" rule; preview-vs-final-mix
  loudness separation; "Encounter defects while working" remediation law.
