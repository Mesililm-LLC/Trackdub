# Bundled manifest schema and profiles

Architecture for `bundled-models.manifest.json` validation, deduplication, and future loader support.

## Files

| File | Role |
|------|------|
| `bundled-models.manifest.json` | Shipping inventory (40 bundled ONNX models) |
| `bundled-models.manifest.schema.json` | JSON Schema contract for manifest entries |
| `bundled-models.profiles.json` | Reusable capability and language-coverage profiles |
| `tools/ci/validate-manifest-schema.py` | CI structural validator (stdlib; mirrors loader rules) |
| `tools/ci/verify-manifest-hashes.py` | HF SHA-256 verification for audited families |
| `tools/ci/audit-bundled-model-manifest.py` | License/commercial gate checks |

## Profiles (phase 1)

`bundled-models.profiles.json` holds canonical lists today used as **reference data**:

- `capabilities.text-refinement-standard`
- `capabilities.translation-direct`
- `capabilities.asr-whisper-auto`
- `language_coverage.whisper-source-auto` (`["auto"]`)
- `language_coverage.qwen3-asr-multilingual`
- `language_coverage.nemotron-asr-multilingual`

Manifest entries still inline their `capabilities` / `language_coverage` values. CI does not require `profile_ref` yet.

## Profiles (phase 2 — planned)

1. Add optional `profile_ref` on manifest entries.
2. Teach `ModelManifestLoader` to expand `profile_ref` into capabilities/language coverage at load time.
3. Migrate duplicated Qwen3/Nemotron language lists to profile references in the manifest.
4. Keep `ModelManifestTests` coverage for expanded catalog shape.

## Schema validation

`validate-manifest-schema.py` enforces:

- Required model fields and allowed `tier` values (`fast`, `balanced`, `quality`, `accurate`)
- `commercial_use_verified` hash evidence (`sha256` or benchmark entry hash)
- `hash_verification.mode=required` completeness for `download_files`, default variant paths, and `benchmark_entry`
- `sha256` alignment with `download_file_hashes[benchmark_entry]` when both are present
- Optional `profile_ref` keys must exist in `bundled-models.profiles.json`

`bundled-models.manifest.schema.json` is the machine-readable contract; the Python validator is the CI gate (no extra Python deps).

## Hash verification CI

| Trigger | Steps |
|---------|-------|
| Pull request | `audit-bundled-model-manifest.py` + `validate-manifest-schema.py` + `verify-manifest-hashes.py --structural --all-families` |
| Weekly cron / manual | Above + `verify-manifest-hashes.py --verify-hf --all-families` (HF resolve downloads) |

Structural mode checks hash completeness and resolve URL buildability without downloading artifacts. HF mode re-verifies pinned digests against Hugging Face.

`cache-installed` revisions (for example sortformer) are skipped in HF mode with explicit warnings; they rely on local cache layout and separate smoke tests.

## Authoring workflow

```powershell
python tools/ci/compute-family-hashes.py --model-id onnx-community/opus-mt-en-es
python tools/ci/verify-manifest-hashes.py --family opus-mt-onnx-community --structural
python tools/ci/verify-manifest-hashes.py --family opus-mt-onnx-community --verify-hf
python tools/ci/validate-manifest-schema.py
python tools/ci/audit-bundled-model-manifest.py
dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~ModelManifest" -m:1
```

Apply scripts (`apply-wave*-commercial-audit.py`) must stay aligned with live manifest layout. Re-running an outdated apply script can regress `download_files` (for example dropping Opus merged-decoder ONNX or Qwen GenAI files).

# Design Spec — G3: Cloud Egress Visibility & Consent

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G3** — local-vs-cloud engine choice is invisible before Run; `CloudAware*` wrappers route silently by `PreferredModelAlias`; data can egress to external providers without user knowledge.

**Relationship to G5:** G3 builds on the readiness panel and `ReadinessState` enum introduced in [design-g5-readiness-gate.md](design-g5-readiness-gate.md). G5's `CloudKeyMissing` state becomes one entry point into G3's consent flow; `CloudEgressConsentRequired` is the new state G3 adds.

**Scope discipline:** this spec covers *disclosure* and *consent* for cloud data egress. Key management (storage, validation) stays in `ApiKeyStore` / `EnvironmentCloudApiKeyProvider`. Model-tier selection UI is out of scope — G3 is about what happens *after* a cloud engine is chosen.

---

## 1. Problem — egress is invisible, consent is absent

**What leaves the machine (confirmed from source):**

*Per-stage pipeline (stages run individually through `TrackdubDubbingEngine.DefaultStageOrder`):*

| Stage | Provider | Data sent | Sensitivity |
|---|---|---|---|
| ASR | OpenAI (`openai-whisper-cloud`) | `File.ReadAllBytes(normalizedAudioPath)` — **entire normalized audio file** (WAV/MP3, raw speech) | **High** — raw audio |
| ASR | Gemini (`gemini-asr-cloud`) | Same — full audio file bytes | **High** |
| Translation | DeepL (`deepl-cloud`) | `segment.Text[]` — transcript text strings | **Med** |
| Translation | OpenAI GPT (`openai-gpt-cloud`) | Same — segment text | **Med** |
| Translation | Gemini (`gemini-translation-cloud`) | Same — segment text | **Med** |
| TTS | ElevenLabs (`elevenlabs-tts-cloud`) | `request.Text` per segment + `voiceId` | **Low-Med** — derived text |
| TTS | OpenAI TTS (`openai-tts-cloud`) | Same — text per segment | **Low-Med** |
| TTS | Google TTS (`google-tts-cloud`) | Same — text per segment | **Low-Med** |

*Cloud dubbing lane (bypasses all stages — `ICloudDubbingEngine`):*

| Lane | Provider | Data sent | Sensitivity |
|---|---|---|---|
| Cloud dub (full pipeline) | ElevenLabs (`ElevenLabsCloudDubbingEngine`) | `File.ReadAllBytes(MediaFilePath)` — **entire source media file** (video+audio, MP4/MKV/WAV) | **Critical** — full video with all audio content |

> **Architecture note:** The cloud dubbing lane is a completely separate path from the per-stage pipeline. `ElevenLabsCloudDubbingEngine.DubAsync` submits the raw media file to `api.elevenlabs.io/v1/dubbing`, polls for completion (up to 30 min), and returns `byte[] AudioBytes`. ElevenLabs handles ASR + translation + TTS internally — Trackdub receives only the final dubbed audio. No transcript, no speaker labels, no segment-level editing.
>
> **Current wiring status:** `ElevenLabsCloudDubbingEngine` is registered in `CompositionRoot` and fully implemented, but **`ICloudDubbingEngine.DubAsync` has no callsite** in the app or SDK as of this writing. The lane is scaffolded, not yet triggered. Consent and disclosure for this lane must be wired **before** any UI trigger is added.

**Three-part failure (updated):**
1. **Per-stage routing is opaque.** `CloudAware*` wrappers route silently by alias — no pre-run disclosure.
2. **No consent gate exists** for any cloud egress path, per-stage or full-pipeline.
3. **Cloud dubbing lane has no consent hook at all** — it's the most severe egress (full video file) and its wiring is deferred, creating risk that it gets connected before consent is designed in.

**Two-part failure:**
1. **Selection → routing is opaque.** `CloudAwareTranslationEngine`/`CloudAwareTtsEngine` choose an engine by matching `PreferredModelAlias` against static predicates (`IsDeepLModelAlias`, `IsElevenLabsAlias`, …). No pre-run disclosure shows which stages will use cloud.
2. **No consent gate.** `StudioSettings` has `AmdRyzenAiLicenseAccepted`, `NvidiaTensorRtRtxLicenseAccepted`, etc. for local EP licenses — but **zero consent fields** for cloud data egress. ASR sends a full audio file to OpenAI with no acknowledgment that this will happen.

**ASR is qualitatively different.** Translation/TTS egress derived text. ASR egresses **raw speech audio** — the actual voice from the source video, before any processing. This distinction must be preserved in the consent model; a flat "allow cloud" toggle collapses it.

---

## 2. Goals / Non-goals

**Goals**
- User knows, before pressing Run, exactly which stages will use cloud and what data category will leave the machine.
- Consent is obtained **once per `(egress-type, provider)` pair**, persisted in `StudioSettings`, and not re-prompted unless revoked.
- The consent model correctly distinguishes **audio egress** from **text egress** — even for the same provider (OpenAI is used for both ASR/audio and GPT-Translation/TTS/text).
- No cloud engine fires without valid consent and a set API key.
- Headless/SDK path has the same gate (no silent cloud calls).

**Non-goals**
- Key management UI — stays in `ApiKeyStore` / Settings `ApiKeysDialog`.
- Network-level key validation — stays in `ApiKeyEntryViewModel.ValidateKeyAsync` (on-demand, not blocking the panel).
- Privacy policy authorship — spec points to where URLs are declared; writing them is out of scope.
- Translation/ASR engine selection UI — choosing *which* cloud engine is out of scope.

---

## 3. Egress type taxonomy

Three consent buckets, not one:

```
EgressType.Audio   — raw audio bytes sent to provider (ASR stage)
EgressType.Text    — transcript or translated text sent to provider (Translation, TTS stages)
EgressType.Media   — entire source media file (video+audio) sent to provider (cloud-dubbing lane)
```

Consent key: `"{egressType}:{providerKey}"`, e.g. `"audio:openai"`, `"text:deepl"`, `"text:elevenlabs"`, `"media:elevenlabs"`.

Full matrix:

| Consent key | Lane | Stage | Provider | Data |
|---|---|---|---|---|
| `audio:openai` | per-stage | ASR | OpenAI Whisper | full audio file |
| `audio:gemini` | per-stage | ASR | Gemini | full audio file |
| `text:deepl` | per-stage | Translation | DeepL | transcript text |
| `text:openai` | per-stage | Translation + TTS | OpenAI GPT + TTS | transcript/translated text |
| `text:gemini` | per-stage | Translation | Gemini | transcript text |
| `text:elevenlabs` | per-stage | TTS | ElevenLabs TTS | translated text + voice ID |
| `text:google` | per-stage | TTS | Google TTS | translated text |
| `media:elevenlabs` | **cloud-dub** | Full pipeline | ElevenLabs Dubbing | **entire source media file** (video+audio) |

Note: `audio:openai` and `text:openai` are **separate consents** — audio egress is distinct from text egress, even to the same provider.

Note: `media:elevenlabs` is the most severe consent key — it gates the cloud dubbing lane. Because `ICloudDubbingEngine.DubAsync` is not yet wired to a UI trigger, this key has no consent prompt today. It **must be gated** before any trigger is added.

---

## 4. `StudioSettings` extension

Add to the `StudioSettings` record (parallel to existing `*LicenseAccepted` booleans, using the same persisted-settings pattern but in a dictionary to avoid N new fields):

```csharp
IReadOnlyDictionary<string, bool>? CloudEgressConsents = null
```

Key format: `"{egressType}:{providerKey}"` (lowercased). `null` / absent key = not consented. Revocation sets the key to `false` or removes it.

Helper (in Application or Contracts):
```csharp
public static class CloudEgressConsentKeys
{
    public const string AudioOpenAi      = "audio:openai";
    public const string AudioGemini      = "audio:gemini";
    public const string TextDeepL        = "text:deepl";
    public const string TextOpenAi       = "text:openai";   // GPT translation + TTS
    public const string TextGemini       = "text:gemini";
    public const string TextElevenLabs   = "text:elevenlabs";
    public const string TextGoogle       = "text:google";
    public const string MediaElevenLabs  = "media:elevenlabs"; // cloud dubbing lane — full video+audio file

    public static string Build(EgressType type, string providerKey) =>
        $"{type.ToString().ToLowerInvariant()}:{providerKey.ToLowerInvariant()}";
}

public enum EgressType { Audio, Text, Media }
```

---

## 5. `CloudEgressDescription` — what gets shown in the consent dialog

```csharp
public sealed record CloudEgressDescription(
    string ConsentKey,             // "audio:openai"
    EgressType EgressType,         // Audio | Text
    string ProviderDisplayName,    // "OpenAI"
    string StageDisplayName,       // "Transcription (ASR)"
    string DataDescription,        // "The full audio from your source video"
    string SentTo,                 // "api.openai.com/v1/audio/transcriptions"
    string? PrivacyPolicyUrl);     // "https://openai.com/policies/privacy-policy"
```

One `CloudEgressDescription` per consent key. Declared as a static catalog (Application layer — no inference/cloud code here, just metadata):

```csharp
public static class CloudEgressCatalog
{
    public static readonly IReadOnlyList<CloudEgressDescription> All = [ ... ];

    public static CloudEgressDescription? Find(string consentKey) =>
        All.FirstOrDefault(d => d.ConsentKey == consentKey);
}
```

---

## 6. Consent flow — when and how

### 6a. Proactive: at engine selection

When the user changes a stage's model override to a cloud alias (in Settings or Run Config), immediately:

1. Compute which consent key(s) the new selection requires.
2. For each key not yet consented: show `CloudEgressConsentDialog` (one per key, sequentially).
3. `CloudEgressConsentDialog` content: egress type badge (Audio 🎙 / Text 📝), provider name, data description, endpoint, privacy policy link, **[Allow] [Not now]** buttons.
4. On **Allow**: persist `CloudEgressConsents[key] = true` via `IStudioSettingsService.SaveAsync`.
5. On **Not now**: selection is NOT reverted (user can keep it), but consent remains absent → stage will be blocked at pre-run backstop.

### 6b. Reactive backstop: pre-run (G5 readiness gate)

`IPipelineReadinessService.EvaluateAsync` adds `CloudEgressConsentRequired` to `ReadinessState`:

- For each enabled stage: if cloud alias selected → look up required consent key(s) → check `StudioSettings.CloudEgressConsents`.
- Status `CloudEgressConsentRequired` → panel badge "☁🔒 consent needed" → resolve action opens `CloudEgressConsentDialog`.
- Run **blocked** while any stage has `CloudEgressConsentRequired` (same backstop logic as `CloudKeyMissing`).

### 6c. Pre-run disclosure strip (non-blocking if consented)

Configure panel shows a per-stage egress summary even when consent is already given — transparency, not a gate:

```
┌─ Run will send data externally ────────────────────────────┐
│  🎙 ASR → OpenAI  (audio)              ✓ consented         │
│  📝 Translation → DeepL  (text)        ✓ consented         │
│  📝 TTS → ElevenLabs  (text)           ✓ consented         │
└────────────────────────────────────────────────────────────┘
```

Only shown when ≥1 cloud engine is active. Zero cloud stages → strip hidden.

---

## 7. Updated `ReadinessState` (extends G5 §5)

G5's state table gains two new entries:

| State | Panel badge | Resolve action |
|---|---|---|
| `CloudKeyMissing` *(G5)* | ☁⚠ "set API key" | API keys dialog |
| `CloudEgressConsentRequired` *(G3 new)* | ☁🔒 "consent needed" | `CloudEgressConsentDialog` |

Both block Run. `CloudEgressConsentRequired` resolves *before* `CloudKeyMissing` in display order (consent is a prerequisite to needing a key — no point entering a key for a provider you haven't consented to use).

---

## 8. `ICloudEgressConsentService` (Application layer)

```csharp
public interface ICloudEgressConsentService
{
    /// <summary>
    /// Returns true if the user has previously consented to this egress key.
    /// Pure read — no I/O beyond reading cached settings.
    /// </summary>
    bool HasConsent(string consentKey);

    /// <summary>
    /// Returns the set of consent keys required for the given stage+alias selection.
    /// </summary>
    IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias);

    /// <summary>
    /// Persists consent (or revocation) for a single key.
    /// </summary>
    Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct);
}
```

Implementation lives in Application, reads/writes `StudioSettings.CloudEgressConsents` via `IStudioSettingsService`. No network calls. Injected into:
- `IPipelineReadinessService` — for stage evaluation
- App VM — for proactive consent at selection time
- Cloud engine wrappers — for assert-only guard (defense-in-depth, see §9)

`GetRequiredConsentKeys` is the lookup table: stage name × alias predicate → consent key(s). Uses the existing static predicates (`AsrModelOverrideSettings.IsCloudAlias`, `TranslationModelOverrideSettings.IsDeepLModelAlias`, etc.) — no new routing logic, just maps them to consent keys.

---

## 9. Defense-in-depth: cloud engine guard

Each cloud engine (`OpenAiCloudTranscriptionEngine`, `DeepLCloudTranslationEngine`, `ElevenLabsCloudTtsEngine`, …) gets an injected `ICloudEgressConsentService` and a **non-interactive assert** at the top of `TranscribeAsync`/`TranslateAsync`/`SynthesizeAsync`:

```csharp
if (!_consentService.HasConsent(_consentKey))
    throw new CloudEgressConsentException(
        $"Cloud egress to {ProviderName} has not been consented. " +
        "Resolve consent in Settings before running this stage.");
```

This is a safety net — the readiness gate and proactive consent should prevent reaching here. Matches "never fake readiness" and "surface encountered problems" invariants.

---

## 10. Headless/SDK path

`TrackdubDubbingEngine` (SDK) gets the same gate via `IPipelineReadinessService`:

- `EvaluateAsync` now also checks consent keys for cloud aliases.
- `RunPreFlightChecksAsync` maps `CloudEgressConsentRequired` → hard fail (add to `preFlightFailures`): `"ASR (audio:openai): cloud egress consent required. Set TRACKDUB_CLOUD_EGRESS_CONSENT=audio:openai,text:deepl or use the app to consent interactively."`
- **Headless consent override:** `DubbingSessionOptions` gains `IReadOnlyList<string>? ExplicitCloudEgressConsents`. If provided, those keys are treated as consented for the run (CI/automation use case — the caller opts in explicitly in code/config rather than via GUI).
- Without `ExplicitCloudEgressConsents` and without persisted GUI consent, headless run with cloud alias **fails fast** before stage loop. No silent audio upload.

---

## 11. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Domain` / `Trackdub.Contracts` | `EgressType` enum; `CloudEgressDescription` record; `CloudEgressConsentKeys` + `CloudEgressCatalog` static class; `ICloudEgressConsentService`; `CloudEgressConsentException`. |
| `Trackdub.Application` | `CloudEgressConsentService` impl; extend `IPipelineReadinessService.EvaluateAsync` to check consent per stage; extend `ReadinessState` with `CloudEgressConsentRequired`. |
| `Trackdub.Infrastructure` | Each cloud engine (`OpenAiCloud*`, `DeepLCloud*`, `ElevenLabsCloud*`, `GeminiCloud*`) gets assert guard via injected `ICloudEgressConsentService`. |
| `Trackdub.Composition` | Register `ICloudEgressConsentService`; wire into cloud engines and readiness service. |
| `Trackdub.Sdk` | `DubbingSessionOptions.ExplicitCloudEgressConsents`; pre-flight maps `CloudEgressConsentRequired` to fail. |
| `Trackdub.App.Avalonia` | `CloudEgressConsentDialog` (new view/VM); proactive consent at selection change; disclosure strip in Run Config panel; consent revocation in Settings/ApiKeys area. |

Layer boundaries preserved: no consent logic in VM beyond calling `ICloudEgressConsentService`; no cloud engine code in App.

---

## 12. Consent catalog (full, at spec time)

| Key | Lane | Stage | Provider | Data description | Endpoint | Privacy URL |
|---|---|---|---|---|---|---|
| `audio:openai` | per-stage | ASR | OpenAI Whisper | Full normalized audio file (WAV/MP3) | `api.openai.com/v1/audio/transcriptions` | https://openai.com/policies/privacy-policy |
| `audio:gemini` | per-stage | ASR | Google Gemini | Full normalized audio file | Gemini AI API | https://policies.google.com/privacy |
| `text:deepl` | per-stage | Translation | DeepL | Transcript text segments | `api.deepl.com/v2/translate` | https://www.deepl.com/en/privacy |
| `text:openai` | per-stage | Translation + TTS | OpenAI GPT + TTS | Transcript/translated text | `api.openai.com/v1/chat/completions`, `.../audio/speech` | https://openai.com/policies/privacy-policy |
| `text:gemini` | per-stage | Translation | Google Gemini | Transcript text segments | Gemini AI API | https://policies.google.com/privacy |
| `text:elevenlabs` | per-stage | TTS | ElevenLabs TTS | Translated text per segment + voice ID | `api.elevenlabs.io/v1/text-to-speech` | https://elevenlabs.io/privacy |
| `text:google` | per-stage | TTS | Google TTS | Translated text per segment | Google TTS API | https://policies.google.com/privacy |
| `media:elevenlabs` | **cloud-dub** | Full pipeline | ElevenLabs Dubbing | **Entire source media file** (video+audio bytes, MP4/MKV/WAV) | `api.elevenlabs.io/v1/dubbing` | https://elevenlabs.io/privacy |

*Catalog is code, not config — update when providers/endpoints change.*

---

## 13. Build sequence (phased)

1. **Contracts/Domain** — `EgressType`, `CloudEgressDescription`, `CloudEgressConsentKeys`, `CloudEgressCatalog`, `ICloudEgressConsentService`, `CloudEgressConsentException`, `StudioSettings.CloudEgressConsents`.
2. **Application** — `CloudEgressConsentService` impl; `ReadinessState.CloudEgressConsentRequired`; extend `EvaluateAsync`.
3. **Infrastructure** — assert guards in each cloud engine.
4. **Composition** — registration.
5. **SDK** — `ExplicitCloudEgressConsents` option; pre-flight fail mapping.
6. **App** — `CloudEgressConsentDialog` + disclosure strip; proactive consent at selection; revocation in Settings.

---

## 14. Tests

- `HasConsent` returns false for absent/null key; true only for explicit `true` in dict.
- `GetRequiredConsentKeys` returns correct key(s) for each cloud alias (covers all 7 combos).
- `EvaluateAsync` returns `CloudEgressConsentRequired` when consent absent; `Ready`/`CloudKeyMissing` when consented but key absent.
- `CloudEgressConsentRequired` blocks pre-run; `ExplicitCloudEgressConsents` unblocks headless.
- Cloud engine guard throws `CloudEgressConsentException` when consent absent (defense-in-depth path, distinct from `InvalidOperationException` for missing key).
- Consent persisted → `LoadAsync` round-trip preserves dict; revocation sets `false`.
- **ASR and text-OpenAI are independent:** consenting `text:openai` does NOT satisfy `audio:openai`.

---

## 15. Open questions

- **Revocation UX:** where does "withdraw consent" live? Settings > API Keys area is the natural home (already has per-provider controls). Confirm with product.
- **`ExplicitCloudEgressConsents` in headless:** should it also accept a file/env-var form for Docker/CI deployments? e.g. `TRACKDUB_CLOUD_EGRESS_CONSENTS=audio:openai,text:deepl`. Low-risk addition; note for SDK phase.
- **Gemini ASR engine:** `GeminiCloudTranscriptionEngine` exists but wasn't read. Assumed same audio-bytes egress as OpenAI (`audio:gemini`). Confirm during Phase 3.
- **Cloud dubbing lane UI trigger:** `ICloudDubbingEngine.DubAsync` has no callsite yet. When a trigger is added it MUST: (1) check `media:elevenlabs` consent first, (2) surface the full-file egress disclosure (this is more severe than TTS text), (3) show that cloud dubbing skips the editable transcript/speaker/segment review steps — ElevenLabs' internal transcript never returns to Trackdub; dubbed audio is muxed directly onto the original video via FFmpeg. Final output is still MP4/MKV. This spec gates the consent architecture; trigger wiring is out of scope here but consent MUST precede it.
- **Cloud dubbing lane + G5 readiness gate:** cloud dub bypasses `DefaultStageOrder` entirely. The G5 `IPipelineReadinessService` targets the per-stage pipeline. A separate readiness check (key present + consent given for `media:elevenlabs`) is needed for the cloud dub path before any trigger is added.

# Design Spec — G4: Run Progress & ETA

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G4** — Run = one click then long blind wait; per-stage progress exists (binary 0%/100%) but no intermediate progress; no ETA; first-run model downloads hide inside stages.

**Relationship to G5:** G5 moves model downloads fully up front (pre-run). G4 specifies what the user sees *during* the stage loop. After G5 lands, Phase 4 is clean inference time → G4's ETA is more accurate. G4 should still gracefully surface download progress in the interim (the `ModelDownloadProgress` infrastructure already has ETR).

**Scope:** progress emission, ETA computation, and surfacing in VM/CLI. No changes to stage execution logic or pipeline sequencing.

---

## 1. Problem — what exists vs. what fires

**Infrastructure present, not wired:**

| Component | Exists | Fires |
|---|---|---|
| `PipelineProgressEventKind.Progress` (kind=1) | ✅ | ❌ never emitted |
| `PipelineProgressEvent.Percentage` (0–100) | ✅ | ❌ always 0 or 100 |
| `PipelineProgressEvent.ElapsedDuration` | ✅ | ✅ at Completed/Failed |
| `ModelDownloadProgress.EstimatedTimeRemaining` | ✅ | ✅ in download dialog only |
| `PipelineStageRowViewModel.IsRunning` | ✅ | ✅ (boolean flip) |
| `PipelineStageRowViewModel.RunProgressText` | ✅ | ❌ never set during run |

**Current behaviour (verbatim from `PipelineRunViewModel`):**
```csharp
_activeStageDisplay = events
    .Select(e => $"{e.StageName}: {e.Percentage:P0}")  // "asr: 0%" entire ASR run
    .ToProperty(...)
```

**Result:** user sees `asr: 0%` for the full duration of ASR (which can be several minutes on first run), with no signal that progress is being made inside the stage.

**Two distinct wait sources:**
1. **Model download/Olive** — G5 moves this up front. Until G5 lands: can run for 1–15 min inside a stage with no progress shown.
2. **ONNX inference** — proportional to audio/segment count. Translation/TTS loop over N segments. ASR processes M VAD regions. VAD/Diarization/Separation are single-pass black boxes.

---

## 2. Goals / Non-goals

**Goals**
- Every in-flight stage shows meaningful intermediate progress (not 0%).
- Segmented stages (Translation, TTS, ASR-by-region) show `N/M segments` + throughput-based ETA.
- Non-segmented stages (VAD, Diarization, Separation) show elapsed time + activity pulse.
- Pipeline-level view shows overall `N/7 stages` + current stage detail.
- Headless/SDK path receives the same `Progress` events for CLI rendering.
- Download progress (when G5 not yet done) is surfaced at the pipeline level, not hidden.

**Non-goals**
- Wall-clock accuracy guarantees — ETA is best-effort throughput projection.
- GPU utilization meter (`GpuUtilization` already a stub in `PipelineRunViewModel` — not addressed here).
- Changing stage execution order or parallelism.

---

## 3. `StageProgressReport` — new progress unit

Add to `Trackdub.Contracts` (or `Trackdub.Sdk`):

```csharp
/// <summary>
/// Intermediate progress report emitted within a single pipeline stage.
/// </summary>
public sealed record StageProgressReport(
    string StageName,

    /// <summary>0–100 percentage. Null for activity-only stages (VAD, Diarization, Separation).</summary>
    double? PercentComplete,

    /// <summary>Items processed so far (segments, regions, chunks).</summary>
    int ItemsComplete,

    /// <summary>Total items. Null when total is unknown up front.</summary>
    int? TotalItems,

    /// <summary>Best-effort remaining time. Null when insufficient history.</summary>
    TimeSpan? EstimatedTimeRemaining,

    /// <summary>Human-readable label for display. E.g. "12 / 38 segments".</summary>
    string? DisplayLabel);
```

---

## 4. Threading progress into stages

### 4a. Extend `TranscriptGenerationContext`

`TranscriptGenerationContext` is an immutable record — add an optional progress reporter:

```csharp
public sealed record TranscriptGenerationContext(
    // ... existing fields ...
    IProgress<StageProgressReport>? StageProgress = null);
```

This is backward-compatible (default null); no change to `ITranscriptGenerationStage` interface.

Stages that can report progress call:
```csharp
context.StageProgress?.Report(new StageProgressReport(
    StageName: StageNames.Translation,
    PercentComplete: 100.0 * done / total,
    ItemsComplete: done,
    TotalItems: total,
    EstimatedTimeRemaining: ComputeEta(done, total, elapsed),
    DisplayLabel: $"{done} / {total} segments"));
```

### 4b. Extend `AsrStageHandler` / `AsrStageRequest`

ASR processes speech regions. Add `IProgress<StageProgressReport>?` to `AsrStageRequest` (or as a handler-level parameter). The handler reports per-region (or per-chunk if batched):

```csharp
// AsrStageHandler.HandleAsync inner loop:
for (int i = 0; i < regions.Count; i++)
{
    // ... transcribe region i ...
    progress?.Report(new StageProgressReport(
        StageNames.Asr,
        PercentComplete: 100.0 * (i + 1) / regions.Count,
        ItemsComplete: i + 1,
        TotalItems: regions.Count,
        EstimatedTimeRemaining: eta.Compute(i + 1, regions.Count),
        DisplayLabel: $"region {i + 1} / {regions.Count}"));
}
```

### 4c. Extend `StartTtsStageHandler` / `TranslationOrchestrationService`

Both already loop over segments — add progress report per iteration. Same pattern as ASR above.

### 4d. Non-segmented stages (VAD, Diarization, Separation)

These are single-pass black boxes with no natural checkpoints:
- Report `StageProgressReport(PercentComplete: null, ItemsComplete: 0, TotalItems: null, DisplayLabel: "running…")` at start.
- Report again with elapsed label at configurable heartbeat intervals (e.g. `PeriodicTimer` at 1s intervals from the calling layer, not from inside the engine itself — keep engine code clean).

---

## 5. ETA computation — `StageThroughputTracker`

Utility in `Trackdub.Application` (no inference dependencies):

```csharp
public sealed class StageThroughputTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _lastComplete;

    /// <summary>Call after each item completes. Returns best-effort ETA or null.</summary>
    public TimeSpan? Report(int itemsComplete, int totalItems)
    {
        if (itemsComplete <= 0 || totalItems <= itemsComplete)
            return null;

        double elapsedMs = _stopwatch.Elapsed.TotalMilliseconds;
        if (elapsedMs < 200) return null;  // too early to project

        double msPerItem = elapsedMs / itemsComplete;
        int remaining = totalItems - itemsComplete;
        return TimeSpan.FromMilliseconds(msPerItem * remaining);
    }
}
```

Simple throughput average. Good enough for segments (typically 10–200). For first-run with no prior data, shows null until 200ms elapsed — then projects.

---

## 6. Pipeline-level aggregation

`TranscriptGenerationPipeline.ExecuteAsync` (and `TrackdubDubbingEngine.ExecuteAsync`) already receive `IProgress<PipelineProgressEvent>`. They need to:

1. Create a `StageProgressAdapter` that converts `StageProgressReport → PipelineProgressEvent(kind=Progress)`:

```csharp
IProgress<StageProgressReport> stageProgress = new Progress<StageProgressReport>(report =>
{
    progress?.Report(new PipelineProgressEvent(
        StageName: report.StageName,
        EventKind: PipelineProgressEventKind.Progress,
        Percentage: report.PercentComplete ?? 0,
        Message: report.DisplayLabel,
        ElapsedDuration: TimeSpan.Zero)  // ETA in Message for now
    );
});
```

2. Thread `stageProgress` into the context:
```csharp
context = context with { StageProgress = stageProgress };
```

3. **Download progress bridge** (until G5 lands): `RuntimeModelSetupWorkflow`'s `callbacks.CreateDownloadProgress` already returns `IProgress<ModelDownloadProgress>`. Bridge it:
```csharp
IProgress<ModelDownloadProgress> downloadProgress = callbacks.CreateDownloadProgress(stageName);
// Wrap to also fire PipelineProgressEvent:
IProgress<ModelDownloadProgress> bridged = new Progress<ModelDownloadProgress>(p =>
{
    downloadProgress.Report(p);
    progress?.Report(new PipelineProgressEvent(
        StageName: stageName,
        EventKind: PipelineProgressEventKind.Progress,
        Percentage: p.PercentComplete,
        Message: $"Downloading model: {p.PercentComplete}%{(p.EstimatedTimeRemaining.HasValue ? $" (~{FormatEta(p.EstimatedTimeRemaining.Value)} remaining)" : "")}",
        ElapsedDuration: TimeSpan.Zero));
});
```

---

## 7. VM changes

### 7a. `PipelineStageRowViewModel` — add progress fields

```csharp
[ObservableProperty]
private double progressPercent;   // 0–100; binds to ProgressBar.Value

[ObservableProperty]
private bool isIndeterminate;     // true for VAD/Diar/Sep while running

[ObservableProperty]
private string? etaText;          // "~23s remaining" | "00:01:42 elapsed" | null
```

No existing fields removed — `RunProgressText` becomes the composite label ("12 / 38 segments").

### 7b. `PipelineRunViewModel` — overall view

Add:
```csharp
[ObservableProperty]
private int stagesComplete;       // M of N done

[ObservableProperty]
private int stagesTotal;          // N (enabled stages this run)

[ObservableProperty]
private string overallElapsedText; // "00:02:14"

[ObservableProperty]
private string? overallEtaText;   // "~4 min remaining" (sum of per-stage ETAs)
```

`PipelineRunViewModel` subscribes to progress events and:
- On `Started`: increment `stagesTotal` (or pre-compute from enabled stages), mark stage `IsRunning=true`.
- On `Progress`: update `ProgressPercent`, `EtaText`, `RunProgressText` on the matching `PipelineStageRowViewModel`.
- On `Completed`/`Skipped`/`Failed`: flip `IsRunning=false`, `ProgressPercent=100` (or 0 for skipped).
- Every 1s: update `overallElapsedText` from a `DispatcherTimer` or Rx interval.

### 7c. Overall ETA

Aggregate per-stage ETAs when available. When a stage has no ETA (black-box stages), show `null` contribution. Display `overallEtaText` only if ≥1 stage provides an ETA:

```
~4 min remaining  (Stage 3/7: Translation — 18 / 52 segments)
```

If no stage-level ETA: show elapsed only — `"00:02:14 elapsed"`.

---

## 8. CLI / headless progress rendering

`CliProgressReporter` already implements `IProgress<PipelineProgressEvent>`. Extend to handle `kind=Progress`:

```
[ASR      ] ████████░░░░░░░░░░░░ 40%  region 8 / 20  (~1m 23s remaining)
[Translate] ████████████████████ 100% completed in 00:00:42
[TTS      ] ████░░░░░░░░░░░░░░░░ 22%  segment 11 / 50  (~3m 10s remaining)
```

---

## 9. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Contracts` / `Trackdub.Sdk` | `StageProgressReport` record; no breaking changes to existing types |
| `Trackdub.Application` | `StageThroughputTracker`; extend `TranscriptGenerationContext` with `IProgress<StageProgressReport>?`; per-segment progress in `AsrStageHandler`, `StartTtsStageHandler`, `TranslationOrchestrationService` |
| `Trackdub.Sdk` | `StageProgressAdapter` (report bridge); `PeriodicHeartbeat` for black-box stages; download bridge; `ReportProgress` fires `kind=Progress` |
| `Trackdub.App.Avalonia` | `PipelineStageRowViewModel.ProgressPercent` + `IsIndeterminate` + `EtaText`; `PipelineRunViewModel` overall view; AXAML binds `ProgressBar` to new fields |
| `Trackdub.Cli` | `CliProgressReporter` handles `kind=Progress` |

Layer boundaries: no inference code in VM; `StageThroughputTracker` in Application with no I/O; progress events remain in Sdk/Contracts.

---

## 10. Build sequence

1. **Contracts** — `StageProgressReport`. No other changes yet.
2. **Application** — `StageThroughputTracker`; extend `TranscriptGenerationContext`; add per-segment reporting to `TranslationOrchestrationService` and `StartTtsStageHandler` first (lowest risk, highest visibility — TTS is often the longest stage).
3. **Application** — `AsrStageHandler` per-region progress.
4. **Sdk** — `StageProgressAdapter` + heartbeat for VAD/Diar/Sep + download bridge + `ReportProgress` fires `Progress` kind.
5. **App** — VM fields + AXAML progress bars. `PipelineRunViewModel` subscription updates.
6. **CLI** — `CliProgressReporter` handles `Progress` kind.

TTS + Translation (step 2) deliver the most visible improvement first. Heartbeat stages (step 4) are pure wrapping — low risk.

---

## 11. Tests

- `StageThroughputTracker.Report` returns null before 200ms; returns finite ETA after; clamps on `itemsComplete >= totalItems`.
- `StageProgressAdapter` maps `StageProgressReport → PipelineProgressEvent(kind=Progress, Percentage=report.PercentComplete)`.
- Translation handler emits N `StageProgressReport` events for N segments (fake `ITranslationEngine`).
- TTS handler emits N events for N speakers' segments.
- ASR handler emits M events for M regions.
- Black-box stages (VAD/Diar/Sep) emit at least one `Progress` event with `PercentComplete=null`.
- Download bridge emits `kind=Progress` with `Percentage = ModelDownloadProgress.PercentComplete`.
- `PipelineRunViewModel`: `stagesComplete` increments on `Completed`/`Skipped`; stage `ProgressPercent` updates on `Progress`; `IsRunning` flips correctly.

---

## 12. Risks / open questions

- **ASR region count:** VAD output is `IReadOnlyList<SpeechRegion>` — count known before ASR starts. Good. But if a cloud ASR engine batches all regions into one HTTP call, per-region progress isn't available — show download-style bytes-received if possible, else indeterminate.
- **Translation batching:** `ITranslationEngine.TranslateAsync` takes a full `TranslationRequest` (all segments at once) — cloud engines (DeepL/OpenAI) are one HTTP call per batch. For cloud, progress is either before/after (binary) or via streaming response parsing. Spec the interface for per-segment progress but accept indeterminate fallback for cloud MT (cloud translation is fast; this is low priority).
- **Thread safety:** `IProgress<T>` callbacks fire on whichever thread calls `Report`. `TranscriptGenerationPipeline` runs on a task thread; `DispatcherTimer` for overall elapsed runs on UI thread. Ensure the Rx `ObserveOn(RxApp.MainThreadScheduler)` in `PipelineRunViewModel` marshals all updates — already present, just verify it covers new fields.
- **G5 ordering:** Download bridge (§6) is a short-lived shim until G5 front-loads provisioning. Mark with `// TODO(G5): remove download bridge once G5 Phase 4 (SDK) lands`.

# Design Spec — G5: Consolidated Pipeline Readiness Gate

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G5** — readiness is inconsistent: front-loaded at import, but post-import tier/diarization/voice changes interrupt mid-run; the headless path throws instead of prompting.

**Decisions (locked with product):**
1. Gate lives in **both** places — a Configure-time readiness panel **and** a pre-run backstop.
2. On selection change after provisioning — **live re-validate + badge** the affected stage.
3. Headless/SDK path — **unify** with the app fix via one shared service.

**Scope discipline:** this spec fixes **G5 only**. The readiness panel exposes a *hook* where per-stage cloud-key status will surface (G3), but cloud-egress consent/visibility behavior is **out of scope** and stays deferred.

---

## 1. Problem — the scatter, with evidence

Readiness today is resolved **inline, per stage, at each stage's trigger**. Each stage runner calls its own `Ensure*ModelAvailableAsync`, whose callbacks drive the modal decision dialog; `!IsReady` aborts the stage with a status message:

| Stage runner (`AvaloniaMainWindowViewModel.PipelineUi.cs`) | Provisioning call |
|---|---|
| `RunAsrStageAsync` (~1394–1432) | `EnsureImportModelsAvailableAsync` + `EnsureDiarizationModelAvailableAsync` (or `EnsureAsrModelAvailableAsync`) |
| `RunDiarizationStageAsync` (~1499) | `EnsureDiarizationModelAvailableAsync` |
| `RunTranslationStageAsync` (~1543) | `EnsureTranslationModelAvailableAsync` |
| `RunTtsStageAsync` (~1591) | `EnsureTtsModelAvailableAsync` (+ inline voice-clone consent) |
| `SegmentEdit.cs` (~364, ~412) | `EnsureTtsModelAvailableAsync` (per-segment regen) |

Three concrete defects fall out:

1. **Scatter.** As the user drives the staged workflow (transcribe → diarize → translate → TTS), each stage's first run can pop a setup dialog. There is no single point where "is this run ready end-to-end?" is answered.
2. **Selection drift.** Each runner rebuilds selections fresh (`CreateDefaultRuntimeSelections()`), so a model-tier change made *after* import causes the next stage's `Ensure*` to discover a new required model → a new mid-workflow dialog.
3. **Snapshot mismatch (latent bug).** `RunDiarizationStageAsync` **provisions** off `CreateDefaultRuntimeSelections()` (~1499) but **executes** off `CreateRuntimeSelections(stateSnapshot)` (~1511). The gate and the work can disagree on which model is in play — G5's bug in miniature. Any fix that evaluates one selection set and runs another simply relocates the defect.

The headless path (`TrackdubDubbingEngine.RunPreFlightChecksAsync`) already loops all stages once before execution, but it is **check-only**: auto-downloadable VAD/ASR/Diar models are explicitly *not* failed and instead download **mid-stage** (the `CanAutoDownload && stageProvisionedDuringExecution` branch, ~265–277). So the two paths are inconsistent and neither truly front-loads provisioning.

---

## 2. Goals / Non-goals

**Goals**
- One **readiness model** shared by the Configure panel, the pre-run backstop, and the headless gate.
- A run is evaluated, provisioned, and executed against **one immutable selection snapshot**.
- Selection edits live-re-evaluate and re-badge without blocking the user.
- Readiness states are **distinct and explicit** per the *never-fake-readiness* invariant — no collapsed Ready/NotReady boolean.

**Non-goals**
- G3 cloud-egress consent/visibility (panel exposes a status hook only — see §9).
- New model formats, new stages, or changes to `DefaultStageOrder`.
- Replacing the existing download/import dialogs — they are reused as the *provision* step's callbacks.

---

## 3. Core model — the selection-snapshot spine *(load-bearing)*

Everything hangs off the distinction between **draft** selections (mutable, edited in the UI) and a **frozen run snapshot** (immutable, the single source of truth for a run).

```
            ┌─ draft selections (UI) ──────────────────────────────┐
 edit tier  │   model tiers · diarization toggle · target lang ·    │
 ─────────► │   per-speaker voices                                  │
            └──────────────┬───────────────────────────────────────┘
                           │  (debounced)
                  Evaluate(draft) ──► PipelineReadinessReport ──► panel + per-stage badges
                           │
            user clicks Run│  FREEZE
                           ▼
                 RunReadinessSnapshot  (immutable; anchored on ExecutionSnapshot)
                           │
              Provision(snapshot) ──► download/import dialogs (batched, once)
                           │
                  Run(snapshot) ──► stage loop reads ONLY the snapshot
```

**Invariant:** `Evaluate`, `Provision`, and `Run` for a given run all read the **same** `RunReadinessSnapshot`. "Live re-validate on change" operates only on the **draft**; pressing Run freezes the draft into the snapshot. This is what structurally prevents defect #3 (and is anchored on the `ExecutionSnapshot` already captured in `TrackdubDubbingEngine`, extended to carry the full `RuntimeModelSelections`).

---

## 4. The Evaluate / Provision split

Today the throw-based per-stage `Ensure*` **conflates** "what's the status?" with "go make it ready." Split them:

### 4a. Read side — `IPipelineReadinessService` (Application/Contracts)
```
PipelineReadinessReport EvaluateAsync(
    IReadOnlyList<RuntimeStage> enabledStages,
    RuntimeModelSelections selections,           // draft (panel) or frozen (gate)
    TranscriptProjectState state,                // for resumable-stage detection
    CancellationToken ct)
```
- Pure read. For each enabled stage: call `IRuntimePlanner.PlanAsync` (already produces `StageRuntimePlan` with `Blocked` / `DownloadRequired` + `Fallback` + `ModelId`), plus a **cloud-key probe** for cloud aliases and a **consent probe** for voice-clone TTS.
- Skips stages with valid existing artifacts (`StageArtifactResumeEvaluator.CanResumeStage`) → reported `Satisfied (resumable)`, no model required.
- **Debounced + cached + per-stage invalidation** (it touches disk and EP probes). Cache key = `(stage, selection-for-stage, source-artifact fingerprint)`; a draft edit invalidates only the stages it affects.

### 4b. Mutate side — `RuntimeModelSetupCoordinator.EnsurePipelineModelsAvailableAsync`
```
RuntimeModelSetupResult EnsurePipelineModelsAvailableAsync(
    TranscriptWorkspace workspace,
    RunReadinessSnapshot snapshot,
    RuntimeModelSetupCallbacks callbacks,
    CancellationToken ct)
```
- Builds the batched request list from the report's not-ready stages and loops the **existing** `RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync` (Download / Import / Skip / Cancel) — one consolidated dialog pass instead of N scattered ones.
- Returns `RuntimeModelSetupResult(IsReady, SkippedStages)`; `SkippedStages` still honors optional **Separation** only.

### 4c. Per-stage `Ensure*` → non-interactive assert
The existing per-stage calls in the stage runners are **demoted, not deleted**: they become a cheap `EnsureModelsAvailableAsync` assert that **throws/logs** if a model is somehow absent (defense-in-depth; still honors *never-fake-readiness*) but **never prompts**. Prompting happens only in the consolidated gate.

---

## 5. Readiness states — *never-fake-readiness* mapping

The report's status type **is** the distinct-states enum the invariant demands, not a boolean. Each maps to a panel badge and a resolve action:

| CLAUDE.md distinct state | `ReadinessState` | Panel badge | Resolve action |
|---|---|---|---|
| provider registered | `ProviderMissing` | ⚠ "no provider" | (config/blocked) |
| runtime installed | `RuntimeMissing` | ⚠ "install runtime/EP" | EP install workflow |
| model files present | `DownloadRequired` / `ImportRequired` | ⬇ "download" / 📁 "import" | Download / Import (existing dialogs) |
| checksum verified | `IntegrityFailed` | ✖ "checksum mismatch" | re-download |
| license reviewed | `LicenseReviewRequired` | 📜 "review license" | `EpVendorLicenseDialog` |
| commercial mode allowed | `CommercialBlocked` | 🚫 "non-commercial blocked" | (blocked; switch model) |
| (cloud) key present | `CloudKeyMissing` | ☁⚠ "set API key" | API keys dialog → §9 hook |
| (consent) clone consent | `ConsentRequired` | 🔒 "consent needed" | `VoiceCloneConsentDialog` |
| ready | `Ready` | ✓ | — |
| stage will be skipped | `Satisfied` (resumable) / `SkippableOptional` (Separation) | ◌ "cached" / "optional" | — |

The panel renders one row per enabled stage with its state; the pre-run backstop refuses to start while any stage is in a blocking state (`ProviderMissing`, `RuntimeMissing`, `Download/ImportRequired`, `IntegrityFailed`, `LicenseReviewRequired`, `CommercialBlocked`, `CloudKeyMissing`, `ConsentRequired`) unless it is resolvable inline.

---

## 6. Components by layer (dependency flow preserved)

| Layer | Change |
|---|---|
| `Trackdub.Contracts` | `ReadinessState` enum; `StageReadiness` + `PipelineReadinessReport` records; `IPipelineReadinessService`; extend `RunReadinessSnapshot` (or reuse `ExecutionSnapshot`) to carry full `RuntimeModelSelections`. |
| `Trackdub.Application` | `PipelineReadinessService` (Evaluate via `IRuntimePlanner` + cloud-key + consent probes, with cache/invalidation); `EnsurePipelineModelsAvailableAsync` on `RuntimeModelSetupCoordinator`; demote per-stage `Ensure*` to assert. |
| `Trackdub.Composition` | Register `IPipelineReadinessService`; both app and SDK resolve the **same** registration. |
| `Trackdub.Sdk` | `TrackdubDubbingEngine.RunPreFlightChecksAsync` → call the shared service; **move provisioning fully up front** and **delete** the `stageProvisionedDuringExecution` mid-stage-download branch (see §8). |
| `Trackdub.App.Avalonia` | RunConfig **readiness panel** (VM binds to `PipelineReadinessReport`); **live revalidation** hook on selection-change; **pre-run backstop** in `RunPipelineStage`; per-stage `Ensure*` calls demoted. No inference/model code added here — VM binds to Contracts only. |

Layer-boundary check: inference/model logic stays in `Inference`/`Application`; the App VM binds to a Contracts DTO; no SQL in VMs; pipeline truth stays in `Application`/`Sdk`.

---

## 7. Live re-validation

- **Triggers:** changes to model-tier pickers, the diarization toggle, translation target language, and per-speaker voice assignment.
- **Mechanism:** each trigger updates the draft selections, **debounces** (~300 ms), then calls `EvaluateAsync` for **only the affected stages** (per-stage cache invalidation) and updates that stage's badge. The frozen snapshot, if any, is marked **stale** so the next Run re-freezes.
- **Non-blocking:** evaluation never opens a dialog; it only recomputes badges. Provisioning dialogs appear solely from the explicit panel "Resolve" button or the pre-run backstop.

---

## 8. Headless unification — extract and share

`unify` = both paths call **one** `IPipelineReadinessService`, and provisioning **moves fully up front** in headless:

- `RunPreFlightChecksAsync` calls `EvaluateAsync` for all stages → if anything is `DownloadRequired`/`ImportRequired` and `CanAutoDownload`, **provision up front** (auto-download) before the stage loop; otherwise return one **aggregated** `PreFlightFailed` listing every unmet stage.
- **Delete** the `stageProvisionedDuringExecution` branch so no model downloads mid-stage. 
- **Behavior change (call out for review):** headless runs now pay all download time *before* the first stage and **fail fast** with a single aggregated error instead of dribbling failures mid-run. This is the intended consistency win; confirm it's acceptable for SDK/CLI consumers.

---

## 9. Edge cases

- **Cloud aliases:** readiness = **API key present** (default; validity check is on-demand, never blocks the panel on a network call). Reported as `Ready` or `CloudKeyMissing`. *This is the single G3 hook — the panel row shows "Cloud (DeepL): key set ✓/✗". Do not extend into egress consent here.*
- **Voice-clone consent:** a TTS readiness requirement when any speaker has a reference clip — reported `ConsentRequired`, resolved by the existing `VoiceCloneConsentDialog`. Keep the dialog; just route it through the report.
- **Optional Separation:** `SkippableOptional`; the gate allows skipping it (matches `IsOptionalRuntimeStage`).
- **Resumable stages:** valid existing artifacts (`StageArtifactResumeEvaluator`) → `Satisfied`, no model required, no badge action.
- **Snapshot mismatch fix:** unify diarization on `CreateRuntimeSelections(snapshot)` for both provision and execute (closes defect #3).

---

## 10. Build sequence (phased, each independently testable)

1. **Contracts** — `ReadinessState`, `StageReadiness`, `PipelineReadinessReport`, `IPipelineReadinessService`, snapshot extension.
2. **Application** — `PipelineReadinessService.EvaluateAsync` (over `IRuntimePlanner`) + `EnsurePipelineModelsAvailableAsync` (batch over `RuntimeModelSetupWorkflow`); demote per-stage `Ensure*` to assert. Unit-tested in isolation with `TestDoubles`.
3. **Composition** — single registration; resolved by both hosts.
4. **SDK** — route `RunPreFlightChecksAsync` to the service; move provisioning up front; delete mid-stage branch. Tests for aggregated-fail + up-front provision.
5. **App** — readiness panel + live revalidation + pre-run backstop; demote per-stage calls.
6. **Cleanup** — fix the diarization selection-snapshot mismatch.

---

## 11. Tests

Cover, per the pipeline-change convention (success / skipped / missing-prereq / failure) plus G5-specifics:
- `Ready` end-to-end; `DownloadRequired` → provision → ready; `ImportRequired` → pick file → ready; `Blocked`/`CommercialBlocked` → backstop refuses.
- **Cancel** in the consolidated gate aborts the run cleanly (no partial stage).
- **Selection change invalidates + re-badges** only the affected stage; frozen snapshot marked stale.
- **Snapshot consistency:** the selections seen by `Evaluate`, `Provision`, and `Run` are identical (regression test for defect #3).
- Cloud alias **key present** vs **key missing**; optional **Separation skip**; **resumable** stage reported `Satisfied`.
- Headless **aggregated failure** lists all unmet stages; headless **up-front provision** downloads before the stage loop (no mid-stage download).

---

## 12. Risks / open questions

- **Evaluate cost** (disk + EP probes): mitigated by debounce + per-stage cache keyed on selection + source fingerprint. Risk: stale cache if an artifact changes underneath — invalidate on artifact-store writes.
- **SDK behavior change** (§8): longer pre-run, fail-fast. Needs explicit sign-off from CLI/API consumers.
- **Cloud key validity** is a network call — default to "present" semantics; only validate on explicit user action to avoid coupling the panel to network state.
- **Open:** does `IRuntimePlanner.PlanAsync` already surface `LicenseReviewRequired` / `CommercialBlocked`, or do those need to be lifted from the manifest/license catalog into the report? Confirm during Phase 2.

# Design Spec — G6 (closed by G3) / G7: Export Provenance & Attribution

## G6 — status: closed by G3

Gap G6: "Cloud-TTS egress unflagged, separate from clone consent."

G3 spec ([design-g3-cloud-egress-visibility.md](design-g3-cloud-egress-visibility.md)) closes this completely. G3's `CloudEgressConsentDialog` + consent keys `text:elevenlabs`, `text:openai`, `text:google` are exactly the per-provider consent gate G6 requires. No additional spec needed for G6.

---

# G7 — Export Provenance & Attribution

**Source gap:** [service-blueprint-first-dub.md](service-blueprint-first-dub.md) · Gap **G7** (Low) — no provenance/attribution in the exported result. Several bundled models require attribution; cloud providers have their own terms. The export doesn't tell the user what was used or what attribution they owe.

---

## 1. Problem — what's missing

`ExportManifest` already captures:

| Field | What's in it |
|---|---|
| `ModelIds` | Distinct TTS model IDs from `TtsTakes.ModelId` **only** |
| `TtsVoices` | Distinct voice IDs from `TtsTakes.VoiceId` |
| `StageRunIds` | Run IDs for transcript + translation + selected TTS takes |
| Per-segment `ModelId`, `VoiceId` | TTS model/voice per segment |

**Not captured:**
- ASR model ID (Whisper variant, Qwen3-ASR, OpenAI Whisper Cloud, etc.)
- Translation engine (opus-mt model, Madlad, DeepL, OpenAI GPT, Gemini)
- Separation model (spleeter, mrx-cocktail-fork)
- Diarization model (sortformer)
- Cloud providers actually used during the run
- Attribution requirements for any model

**Models with `requires_attribution: true` in bundled manifest (confirmed):**

| Model ID | Stage | License |
|---|---|---|
| `csukuangfj/sherpa-onnx-spleeter-2stems` | Separation | MIT |
| `tonythethompson/mrx-cocktail-fork-onnx` | Separation | MIT |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | Diarization | NVIDIA-Open-Model-License |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | TTS | Apache-2.0 |
| `onnx-community/opus-mt-*` family | Translation | CC-BY-4.0 |

**Models with `requires_attribution: false`:** Whisper family, Silero VAD, Qwen3-ASR, Qwen2.5 — these need no attribution.

**Result:** a user who exports with spleeter + sortformer + kokoro has attribution obligations they cannot see. Cloud providers (DeepL, ElevenLabs, etc.) require their own disclosures. The export manifest and UI are silent on all of this.

---

## 2. Goals / Non-goals

**Goals**
- Export manifest records **all** contributing models (all stages), not just TTS.
- Attribution requirements for `requires_attribution: true` models are surfaced in the manifest and in the export success UI.
- Cloud providers used are recorded (matched from G3 consent keys — no new egress logic here).
- Export sidecar JSON (`{filename}.export-manifest.json`) is the single source of truth for compliance.

**Non-goals**
- Generating attribution text prose — spec records structured data (model ID, license, attribution URL); rendering human prose is a UI concern.
- Legal review of attribution requirements — spec surfaces what the manifest says; legal interpretation is out of scope.
- Provenance for preview mixes — only the final export.
- Backfilling old exports.

---

## 3. Per-stage model capture — where the data lives

Each stage has a `StageRunRecord` and optionally a `StageRuntimeExecutionSummary` (via `IStageRuntimeExecutionReporter`). The summary carries:

```csharp
StageRuntimeExecutionSummary(
    string RequestedProvider,
    string SelectedProvider,
    string ModelAlias,         // ← the model that actually ran
    string BootstrapDetail)
```

`ModelAlias` maps 1:1 to a manifest entry (e.g. `"whisper-tiny-onnx"`, `"sortformer-4spk"`, `"kokoro-onnx"`, `"deepl-cloud"`).

**Collection point:** `ExportStageHandler.GetContributingStageRuns()` already selects the `StageRunRecord` list. Extend it (or its callsite) to also collect `ModelAlias` per stage run.

**Persistence:** `StageRunRecord` currently stores `Status`, `StartedAtUtc`, `CompletedAtUtc` etc. Extend to include `ModelAlias`/`ProviderAlias` or retrieve from a `StageRunDetail` store. See §6 for the least-invasive hook.

---

## 4. `ExportManifest` extension

Add two new top-level fields:

```csharp
public sealed record ExportManifest(
    // ... existing fields unchanged ...

    /// <summary>All models that contributed to this export, across all stages.</summary>
    IReadOnlyList<ExportManifestModel> ContributingModels,

    /// <summary>Attribution requirements derived from contributing models with requires_attribution=true.</summary>
    IReadOnlyList<ExportAttributionRequirement> AttributionRequired);
```

### `ExportManifestModel`

```csharp
public sealed record ExportManifestModel(
    string Stage,              // "asr" | "translation" | "tts" | "separation" | "diarization"
    string ModelAlias,         // "whisper-tiny-onnx", "deepl-cloud", "kokoro-onnx", ...
    string? ModelId,           // full HF model id if local; null for cloud
    bool IsCloud,              // true for deepl-cloud, openai-*, etc.
    string? CloudProviderKey,  // "deepl" | "openai" | "gemini" | "elevenlabs" | "google" | null
    string? License,           // "MIT" | "Apache-2.0" | "CC-BY-4.0" | null (unknown for cloud)
    bool RequiresAttribution); // from bundled manifest; false for cloud engines
```

### `ExportAttributionRequirement`

```csharp
public sealed record ExportAttributionRequirement(
    string ModelAlias,
    string Stage,
    string License,
    string? AttributionText,   // "sortformer by NVIDIA, licensed under NVIDIA-Open-Model-License"
    string? SourceUrl);        // HF model page or project URL
```

---

## 5. Attribution catalog

Static catalog in `Trackdub.Application` (no inference dependencies). Populated from `bundled-models.manifest.json` at build/compose time. The manifest already has `requires_attribution`, `license`, `source_url`, `requires_user_consent` — all needed fields are present.

```csharp
public static class ModelAttributionCatalog
{
    // Keyed by model alias (normalized lowercase)
    private static readonly IReadOnlyDictionary<string, ModelAttributionEntry> _entries;

    public static ModelAttributionEntry? Find(string modelAlias) =>
        _entries.TryGetValue(modelAlias.ToLowerInvariant(), out var entry) ? entry : null;

    public static bool RequiresAttribution(string modelAlias) =>
        Find(modelAlias) is { RequiresAttribution: true };
}

public sealed record ModelAttributionEntry(
    string ModelId,
    string ModelAlias,
    string License,
    bool RequiresAttribution,
    string? SourceUrl,
    string? AttributionText);
```

`ModelAttributionCatalog` is initialized from the manifest at composition time (injected as `IModelAttributionCatalog` so it's testable). Cloud engine aliases (`deepl-cloud`, `openai-tts-cloud`, etc.) map to `RequiresAttribution: false` with `License: "cloud-service-terms"`.

---

## 6. Data collection — `ExportManifestBuildRequest` extension

Extend `ExportManifestBuildRequest` with a new field:

```csharp
IReadOnlyList<StageModelRecord>? StageModels = null
```

```csharp
public sealed record StageModelRecord(
    string StageName,
    string ModelAlias,
    bool IsCloud,
    string? CloudProviderKey);
```

**Where to populate it:** `ExportStageHandler.BuildManifest()` calls `ExportManifestBuilder.Build(request)`. The handler has `currentState` which includes `StageRuns`. Each `StageRunRecord` needs a `ModelAlias` field added (nullable, opt-in — existing runs without it gracefully produce null entries).

**Least-invasive hook** (preferred over full StageRunRecord schema change):

Option A — extend `StageRunRecord` with optional `ModelAlias`: cleanest; one migration.
Option B — `StageRunDetail` lookup table keyed by `StageRunId` → `ModelAlias`: no migration; new table.

Recommend **Option A** — `StageRunRecord` already stores `ExecutionProvider`, `ModelAlias` is the natural companion. Add it as nullable string; existing SQLite rows read null → manifest shows "unknown".

---

## 7. Export manifest builder changes

`ExportManifestBuilder.Build()` gains:

```csharp
IReadOnlyList<ExportManifestModel> contributingModels = BuildContributingModels(request, catalog);
IReadOnlyList<ExportAttributionRequirement> attributionRequired =
    contributingModels
        .Where(m => m.RequiresAttribution)
        .Select(m => BuildAttributionRequirement(m, catalog))
        .ToArray();

return new ExportManifest(
    // ... existing fields ...
    ContributingModels: contributingModels,
    AttributionRequired: attributionRequired);
```

`BuildContributingModels`:
1. Collect TTS model IDs from `TtsTakes.ModelId` (existing) — stage = "tts"
2. Collect per-stage model aliases from `request.StageModels` — stages asr/translation/separation/diarization
3. Deduplicate by `(Stage, ModelAlias)`.
4. For each: lookup `ModelAttributionCatalog.Find(alias)` for `IsCloud`, `License`, `RequiresAttribution`.

---

## 8. Export success UI — attribution surface

After export completes, the `ExportMixView`/`ExportMixViewModel` shows:

```
✓  Export complete  →  dubbed.mp4  [Show in folder]

Models used:
  ASR        whisper-tiny-genai    (local, no attribution required)
  Diarization sortformer-4spk      (local) ★ attribution required
  Translation deepl-cloud           (DeepL cloud service)
  TTS        kokoro-onnx            (local) ★ attribution required

Attribution required for this export:
  • sortformer by NVIDIA — NVIDIA-Open-Model-License
    https://huggingface.co/cgus/diar_streaming_sortformer_4spk-v2.1-onnx
  • Kokoro-82M by Hexgrad — Apache-2.0
    https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX

Attribution details saved to: dubbed.export-manifest.json
```

If no attribution required and no cloud: show nothing extra (don't add noise for the common local all-no-attribution case).

`ExportMixViewModel` binds to:
```csharp
IReadOnlyList<ExportManifestModel> ContributingModels { get; }
IReadOnlyList<ExportAttributionRequirement> AttributionRequired { get; }
bool HasAttributionRequired => AttributionRequired.Count > 0;
bool HasCloudProviders { get; }
```

---

## 9. Components by layer

| Layer | Change |
|---|---|
| `Trackdub.Contracts` | Extend `StageRunRecord` with `string? ModelAlias` (nullable, migration-safe). |
| `Trackdub.Application` | `ModelAttributionEntry` record; `IModelAttributionCatalog` + `ModelAttributionCatalog` static impl; `ExportManifestModel` + `ExportAttributionRequirement` records; extend `ExportManifest` + `ExportManifestBuildRequest` + `ExportManifestBuilder`; extend `StageRunHelper.CompleteAsync` to accept optional `modelAlias`. |
| `Trackdub.Infrastructure` | SQLite migration: add `ModelAlias` column to stage runs table (nullable, no-op on existing rows). |
| `Trackdub.Composition` | Register `IModelAttributionCatalog` populated from `bundled-models.manifest.json` at startup; inject into `ExportManifestBuilder`. Extend each stage handler's `CompleteAsync` call to pass `executionSummary.ModelAlias`. |
| `Trackdub.App.Avalonia` | `ExportMixViewModel`: bind `HasAttributionRequired`, `ContributingModels`, `AttributionRequired`; show attribution section in `ExportMixView.axaml` when `HasAttributionRequired`. |

No inference code in App; no SQL in VMs; layer boundaries preserved.

---

## 10. Build sequence

1. **Contracts** — extend `StageRunRecord` with `ModelAlias?`.
2. **Application** — `ModelAttributionEntry`, `IModelAttributionCatalog`, `ExportManifestModel`, `ExportAttributionRequirement`; extend `ExportManifest` + builder.
3. **Infrastructure** — SQLite migration + extend stage run persistence to write `ModelAlias`.
4. **Composition** — catalog registration; each stage handler passes `ModelAlias` on complete.
5. **App** — `ExportMixViewModel` fields; `ExportMixView` attribution section.

---

## 11. Tests

- `ExportManifestBuilder`: empty `StageModels` → `ContributingModels` has TTS models only (backward compat).
- With `StageModels`: all stages reflected; deduplication works.
- `AttributionRequired` contains only entries where `RequiresAttribution=true`.
- Cloud alias (e.g. `"deepl-cloud"`) → `IsCloud=true`, `RequiresAttribution=false`, `CloudProviderKey="deepl"`.
- `ModelAttributionCatalog.Find("kokoro-onnx")` → `RequiresAttribution=true`, `License="Apache-2.0"`.
- `ModelAttributionCatalog.Find("whisper-tiny-onnx")` → `RequiresAttribution=false`.
- Export with no attribution-required models → `AttributionRequired` empty; UI attribution section hidden.
- SQLite migration: existing stage run rows with null `ModelAlias` → manifest `ContributingModels` omits those entries (no crash).

---

## 12. Open questions

- **StageRunHelper.CompleteAsync signature:** currently takes `runtimeReporter` (the reporter instance). Extract `ModelAlias` from it there, or pass as explicit param? Extracting from reporter is cleaner — no new param to existing callers.
- **Opus-MT attribution text:** CC-BY-4.0 requires attribution, but the 13 opus-mt pairs each have their own HF model page. Show one entry per model used or one "opus-mt family" entry? One-per-model is more precise; merge if user feedback shows it's overwhelming.
- **Cloud service terms:** DeepL/OpenAI TTS/ElevenLabs terms of service require their own acknowledgments beyond what G3 consent covers. Not specced here — flag for legal review.

# Pipeline transient-failure coverage spec

> Current AWS POC contract: [SnapSoft application acceptance](../operations/snapsoft-poc-acceptance.md). Historical API/Swagger/compose/telemetry examples below are not evidence of current wiring. The new CPU API uses DynamoDB/S3, contains no inference, and has no billing/OTel exporter. GPU compose runs the one-shot CLI; it does not expose HTTP.

**Status:** Draft (2026-07-22). Spec only; does **not** close a BACKLOG row.
**Lane:** Fault tolerance, pipeline-wide orchestrator, cross-cutting reader surface.
**Coverage:** Cancellation + directory/file lock + transient download + transient OOM.
**Cross-platform parity:** `net10.0` + `net10.0-windows10.0.19041.0` + Linux/macOS shared tail; Avalonia UI tier stays on Windows TFM.
**Audience:** engineers + headless SDK/CLI/Worker + Avalonia VM + Trackdub doctor handler.

## 1. Problem statement

Trackdub's pipeline already labels failures ("Blocked", "Failed", "Skipped — valid artifacts from prior run", "Failed: speech-enhancement: …") and emits a sequence of `PipelineProgressEvent` records plus a SQLite `StageRunRecord` per stage. But transient failures — ones that should not turn the run into a hard fail and that the user should understand are recoverable — are not classified, surfaced, or counted anywhere.

Evidence:
- `src/Trackdub.Application/Dubbing/DubbingPipelineEngine.cs:1314-1316` — `IsBenignSkipReasonCode` only knows about a small set of benign codes (e.g. `EXISTING_ARTIFACTS_VALID`); everything else falls into the "failed" bucket.
- `src/Trackdub.Domain/StageRuns/StageSkipReasonCodes.cs:8-29` — single constant `ExistingArtifactsValid` plus the benign set; no enum.
- `src/Trackdub.Application/Transcripts/StageRunHygiene.cs:13-58` — only reconciles stale `Running` rows after crash, not transient failures during a live run.
- `src/Trackdub.Application/Transcripts/Pipeline/TranscriptPipelineBuilder.cs:82-110` — the resume path silently skips without an upstream "transient" marker.
- `src/Trackdub.Application/Transcripts/Pipeline/TranscriptPipelineResumeHydrator.cs` — re-uses prior artifacts with `Status == Completed`; cannot tell apart a transient failure that snuck in before a successful artifact.
- `src/Trackdub.Inference/Pool/InferenceRetryPolicy.cs:70` — already pulls `[ErrorCode:XXX]` from `OnnxRuntimeException` but only paper-trails; never publishes to a stream.
- `src/Trackdub.Infrastructure/Licensing/ParallelRangeDownloader.cs:70-87` — stopwatch + bytes/sec; `CancellationToken` flow honored but no classification.
- `src/Trackdub.App.Avalonia/Services/OperationRunner.cs:30-69` — `OperationRunnerLane.Load` vs `Pipeline` lane semantics; cancellation propagation is correct but untyped.
- Avalonia `PipelineStageRowViewModel.cs:265-320` — only knows Completed/PartiallyCompleted/Skipped/Running/Failed; no "transient retry" state.
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs` — current section list does not include a transient-fault summary; no end-to-end postmortem correlation.

Net effect: a transient cancel or download-retry storm looks identical to a real failure in the SQLite rows, the UI row icon, the run-manifest JSON, and the diagnostics bundle. There is no honest signal for "the user pressed Cancel halfway through ASR" vs "ASR failed". The acceptance posture "never fake readiness / no silent failures / honest per-stage states" (per `MILESTONE.md` §Current non-negotiables) cannot be satisfied for transient cases today.

## 2. Goals

1. Define one typed `TransientFailureKind` enum that classifies the four modes in scope plus a small set of near-cousins. Subscribers can pattern-match on the kind, not string-match on ad-hoc reason codes.
2. Introduce one `PipelineTransientFault` record (per-fault signal) + one `PipelineTransientFaultBus` (in-process pub/sub bounded to the last 50 faults). Three writers, four readers minimum.
3. Surface the bus three ways:
   - Through `DubbingRunStatus`/`StageRunRecord` so the SQLite state is honest about transient retries.
   - Through `RunManifest.transient` key so SDK/CLI/Worker/headless batch consumers can audit.
   - Through a new `DiagnosticsBundle.transient` section so post-mortem docs include them.
4. Add `WorkerMetrics.transient_total` counter.
5. Wire the existing `CancellationToken` flow at `StageRunHelper.RunStageAsync` so it writes a `StageRunRecord` with `Status = Canceled` *before* re-throwing — no half-state where SQLite still says Running.
6. Tests cover each of the four transient classes (cancel, lock, download, OOM) × each of the four read surfaces (StageRunRecord, RunManifest, DiagnosticsBundle, Avalonia row).

## 3. Non-goals

- Permanent-failure contract extension stays out of scope; existing `StageSkipReasonCodes.IsBenignSkipReasonCode` continues to govern the benign-skip path.
- This spec does **not** touch `AvaloniaMainWindowViewModel` UI chrome beyond a single `LastTransientText` string on `PipelineRunViewModel`. Visual refresh is in a follow-up spec.
- No new external telemetry sinks (no OpenTelemetry, no Sentry). The bus is in-process only.
- No SDK breaking change beyond a new additive `IAsyncEnumerable<PipelineTransientFault>` accessor on `IDubbingPipelineEngine` and a new `RunManifest.transient` key. Old fields untouched.
- OOM classification remains best-effort heuristic — a precise OOM-by-CPU-budget ADR is its own future backlog candidate.
- No backport to closed BACKLOG rows. P0-1 evidence from 2026-06-10 stays valid; this is a parallel track.

## 4. Proposed design

### 4.1 `TransientFailureKind` enum
- Lives in `src/Trackdub.Domain/Pipeline/TransientFailureKind.cs`.
- Members:
  - `UserCancellation` — caller's `CancellationToken` fired; never retry, write `Canceled` row.
  - `DirectoryLock` — a process or app holds the directory/file exclusively; retry with backoff.
  - `SqliteBusy` — SQLite `SQLITE_BUSY`; retry, same backoff.
  - `FfmpegProcessExit` — ffprobe/ffmpeg crashed mid-op (often transient driver state); retry.
  - `ModelDownloadTransient` — HF/mirror returned 5xx, network glitch; retry.
  - `StarterPackTransient` — 7zr crash or tar exit non-zero + archive integrity OK; retry.
  - `MemoryExhausted` — inference host ORT/OnnxRuntime reported memory pressure; backoff + downscale quant.
  - `DeviceTimeoutTransient` — DirectML/TensorRT-RTX/WinML catalog stalled on a hot plug; retry.
  - `Unknown` — fallback when no classifier matches; logged, classified as retriable once.
- Static helper `bool IsTransient(Exception ex)` returns true iff the exception's type or message maps to any of the above. Defaults false (fail fast).

### 4.2 `PipelineTransientFault` record
- Lives in `src/Trackdub.Contracts/Pipeline/PipelineTransientFault.cs`.
- Fields: `Guid ProjectId`, `string StageName`, `TransientFailureKind Kind`, `string Detail`, `DateTimeOffset HappenedAt`, `int AttemptNumber`, `IReadOnlyDictionary<string,string>? Context` (free-form: path, exception type, exit code, etc.).

### 4.3 `PipelineTransientFaultBus`
- Lives in `src/Trackdub.Application/Transcripts/Pipeline/PipelineTransientFaultBus.cs`.
- Singleton, scoped per project run via `projectSession`. Bounded ring buffer (last 50). Exposes `IObservable<PipelineTransientFault> Stream` for live subscribers.
- Method `Publish(PipelineTransientFault fault)` is idempotent under cancellation (publishes nothing if the parent `CancellationToken` is already cancelled unless the fault's Kind is `UserCancellation` itself — exception so the user-action event is never silenced).
- Snapshot accessor `IReadOnlyList<PipelineTransientFault> Snapshot()` for the postmortem write.
- **Shipped scope (as of the §4.4/§4.5 wiring PR):** the bus is registered as a Composition-level singleton (`HeadlessCompositionRoot.AddHeadlessTrackdub` → `services.AddSingleton<PipelineTransientFaultBus>()`), not scoped per project run. `DubbingPipelineEngine` and `DiagnosticsBundleExporter` both resolve the same instance for the lifetime of the host, so faults from different runs share one ring buffer. Per-run scoping remains a candidate follow-up (see §9.1).

### 4.4 Wiring at `StageRunHelper.RunStageAsync`
- This is the single chokepoint across `Vad`, `Asr`, `Diarization`, `SpeechEnhancement`, `StemSeparation`, `SpeakerAssignment`, `Translation`, `TTS`, `LipSync`, `LipSynthesis`, `OverlapRescue`, `Export`. One wiring edit covers all.
- Pattern: `try { ...existing body... }` extended with:
  - `catch (OperationCanceledException)` → emit `UserCancellation`, write `StageRunRecord.Status = Canceled` via `StageRunHelper.RecordCancelAsync`, re-throw.
  - `catch (Exception ex) when (TransientFailureKind.IsTransient(ex))` → emit `((kind, attempt))`, leave `Status = Running` for one more retry attempt, re-throw.
- A small retry helper `RunStageWithTransientRetryAsync(...)` does up to 3 attempts of the inner body, doubling backoff (50ms, 100ms, 200ms) per same-kind fault within the same stage.
- **Shipped behavior differs from the bullet above:** `StageRunHelper.RunStageAsync`'s transient catch has no retry loop of its own, so it now calls `FailAsync` (terminal `Failed` row) before re-throwing instead of leaving `Status = Running`. Leaving the row `Running` only applies inside `RunStageWithTransientRetryAsync`, which owns its own `StageRunRecord` lifecycle across attempts. Callers that want retry semantics must go through `RunStageWithTransientRetryAsync`; direct `RunStageAsync` callers always see a terminal row on a transient failure.
- **Shipped follow-up — retry-budget extraction:** the 3-attempt / doubling-backoff parameters above are no longer hardcoded inline. They live in the `StageRetryBudget` domain record (`src/Trackdub.Domain/Pipeline/StageRetryBudget.cs`): `MaxAttempts` (1–10), `BaseBackoffMs` (>= 0), `MaxBackoffMs` (default 51,200ms), with `BackoffFor(attempt)` computing the doubling delay. `RunStageWithTransientRetryAsync` takes an optional `retryBudget` parameter and falls back to `StageRetryBudget.Default` (`MaxAttempts: 3, BaseBackoffMs: 50`), which reproduces the 50ms/100ms/200ms sequence above unchanged. Callers may inject a tighter budget (e.g. `MaxAttempts: 1`) without touching the retry-loop body. See §11.9.

### 4.5 Reader surfaces

- `DiagnosticsBundleExporter`: append a `transient` section with `Total`, `CountsByKind`, `MostRecent[]` (max 20). Schema in `src/Trackdub.Infrastructure/Diagnostics/TransientFaultSummary.cs`. Persisted in the run-manifest JSON under the same key.
- `IDubbingPipelineEngine`: gain `IAsyncEnumerable<PipelineTransientFault> TransientFaults { get; }`. Stream consumers in `Trackdub.Cli` (DoctorHandler `--explain-transient`), `Trackdub.Worker` (counter emissions), `Trackdub.Sdk` (tests).
  - **Shipped shape:** the surface landed as a separate `ITransientFaultReporting.TransientFaultsAsync(CancellationToken cancellationToken = default)` method (not a `TransientFaults` property on `IDubbingPipelineEngine`), implemented by both `DubbingPipelineEngine` and the SDK's `TrackdubDubbingEngine` (which forwards to the inner engine). It bridges the bus's `IObservable<PipelineTransientFault>` into a bounded `Channel`-backed `IAsyncEnumerable`, so subscribers see live faults for the duration of enumeration rather than a one-time snapshot.
- `AvaloniaMainWindowViewModel.PipelineUi`: bind `PipelineRunViewModel.LastTransientText` (string) updated via `ApplyTransientFault` event handler. UI row icon stays the existing one; transient overlay badge shows "retrying (N)" while `applied Faults.Count > 0`. After all settle, transient counters become invisible unless doctor / diagnostics open them.
- `Trackdub.Worker/WorkerMetrics`: emit `transient_total` counter per stage × per kind.

### 4.6 Cancellation token honesty

- `OperationRunner.TryRunAsync` already threads `CancellationToken`. The orchestrator must:
  - Honor the token at every `await`.
  - On cancel, write the canceled `StageRunRecord` row *before* re-throwing so SQLite doesn't lie about an `Running` row that was actually canceled halfway.
- This is a one-line addition to the catch block; the existing `OnFrameworkInitializationCompleted` crash scaffolding in `App.axaml.cs` is upstream of this concern and stays untouched.

## 5. Acceptance criteria

- `dotnet build Trackdub.sln -m:1 -p:Platform=x64 --no-restore` clean (Trackdub.Avalonia multi-target + Windows-only test TFM allowed).
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Worker.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` 100% green.
- `dotnet test tests/Trackdub.Cli.Tests --no-restore -m:1 --filter "FullyQualifiedName~Transient"` 100% green.
- `dotnet format --verify-no-changes` clean for every touched project.
- When the headless smoke runs (`tests/Trackdub.Sdk.Tests/HeadlessPipelineSmoke`) with a synthetic transient in the stub providers, `run-manifest.json` gains a top-level `transient: { countsByKind: {...}, mostRecent: [...] }` shape matching §4.5 schema.
- Doctor handler enumerates the new `TransientFailureKind` codes via an `--explain-transient <kind>` flag in `Trackdub.Cli/Handlers/DoctorHandler.cs`.
- Avalonia `PipelineRunViewModel` tests pass when `ApplyTransientFault` receives a sequence of three `DirectoryLock` events then a success — `LastTransientText` reflects the latest.

## 6. Tests plan

- **Unit (Domain)**: `TransientFailureKind.IsTransient(ex)` returns expected bool for: `OperationCanceledException`, `IOException` with `[ErrorCode: 32]` (locked), `SqliteException` with `SQLITE_BUSY`, `OnnxRuntimeException` with `[ErrorCode: 4]`, `HttpRequestException` with 5xx, `OutOfMemoryException`. Plus negative cases (`ArgumentException`, `NullReferenceException`) returning false.
- **Unit (Application)**: `PipelineTransientFaultBus` publish + 50-cap overflow correctness. Snapshot order = arrival order.
- **Integration (Application)**: `StageRunHelper.RunStageWithTransientRetryAsync` with a fake that throws on the first N attempts and succeeds on the N+1. Verify bus receives N events of same kind and final StageRunRecord has `Status = Completed`.
- **Integration (SDK/Worker)**: a stub DubbingPipelineEngine that emits three `ModelDownloadTransient` events; assert `IAsyncEnumerable<PipelineTransientFault> TransientFaults` yielded all three; `WorkerMetrics.transient_total` = 3.
- **Headless**: `HeadlessPipelineSmoke` with a fake stage that throws one `DirectoryLock`; assert `run-manifest.json.transient.countsByKind.DirectoryLock = 1` and the snapshot diagnostic export contains a `transient` section.
- **Avalonia**: PipelineRunViewModel tests asserting `ApplyTransientFault` updates `LastTransientText` correctly across cancel / lock / download / success sequences. UI Tests may stay on Windows TFM; the binding logic is TFM-agnostic.
- **Regression**: existing `PipelineDegradationWriterTests`, `AsrDeviceDegradationTests`, `HeadlessPipelineSmokeTests` must still pass unchanged.

## 7. Cross-platform notes

- Avalonia UI tier stays `net10.0-windows10.0.19041.0` for Windows-only tests. The VM, bus, and types are TFM-agnostic.
- Linux/macOS: file-lock semantics differ. `DirectoryLock` classification uses `IOException` with `HResult == 0x80070020` (ERROR_SHARING_VIOLATION) on Windows and `[Errno 11] EAGAIN` / `[Errno 35] EDEADLK` on POSIX; classifier is platform-gated via `#if WINDOWS / #elif LINUX / #elif MACOS`.
- Storage paths that differ by platform: `IOException.HResult` on POSIX is `Marshal.GetLastWin32Error` style not directly available — the classifier uses `ex.Message` regex on POSIX as fallback.

## 8. Risk + alternatives (recorded for future ADR candidates)

- (a) Bounded bus cap (50). A 1000-fault/minute retry storm could overwrite the first 950. Alternative: stream-only; no snapshot file. Trade-off favors bounded + snapshot for repr() stability under Android-like storms.
- (b) OOM classification. Heuristic by exception type + log message. False positives are possible (legitimate `NullReferenceException` with "OOM" in message). Future ADR can tighten.
- (c) Backwards-compat of `RunManifest` JSON. Adding a top-level `transient` key is additive and does not break existing schema. The spec treats everything else as frozen.
- (d) Whether `TransientFailureKind` belongs on Domain or Contracts. Domain is currently chosen (the enum is purely a data shape, not an SDK API). SDK imports Domain for stream element type. If a future ADR rejects that, an option is `Contracts.Pipeline.TransientFailureKind` with a Domain re-export.

## 9. Open Questions — ADR candidates (fleshed)

Each question below is structured for promotion to a discrete ADR. Status: **Candidate** (not yet numbered; this spec does not own the ADR sequence). Promotion criteria are explicit per question.

### 9.1 Candidate ADR: per-run aggregation strategy

- **Slug:** `pipeline-transient-aggregation`

**Problem.** §4.3 ships a 50-event ring buffer + per-stage counters. The companion question is whether the snapshot the bundle exposes should also aggregate across stages into a single per-run summary (e.g. `transient.countsByKind` summed across all stages of one project) — and where that aggregation lives.

**Options considered.**

- (a) Snapshot per-run only via the existing `DiagnosticsBundle.transient` section. No in-memory aggregate; per-stage counts only.
- (b) In-process `PipelineTransientFaultBus.SnapshotPerRun(Guid projectId)` returning a per-stage × per-kind roll-up. Filter at the bus boundary, not at the consumer.
- (c) SQLite runtime rollup table `PipelineTransientFaultCounts` updated synchronously on every `Publish`. Persisted, queryable, but adds a write per fault.
- (d) Stream-only; aggregate callers re-filter from `IObservable` with their own windowing.

**Trade-offs.** (a) is light but loses cross-stage correlation. (b) keeps SQLite schema frozen, gives fast aggregate, degrades cleanly when the bus is dropped. (c) yields persistence for free but adds latency to every fault and a new migration; per-`Publish` SQLite roundtrip is wasteful. (d) puts the work on every consumer and forces everyone to re-rollup.

**Recommendation.** (b). It honours the spec’s “schema frozen, additive only” gate, caps in-proc memory at the existing 50-event ring, and the per-stage snapshot in the bundle stays accurate without a write storm. Persisted rollups can be derived later if needed.

**Promotion criteria.** Promote to a discrete ADR when one or more of the following is true:
- A new consumer (e.g. the Web dashboard or a future hosted variant) asks for cross-stage fault aggregation across a run.
- A user-facing report (e.g. “this run had 14 transient faults and your project retried 9 times”) is added to the UI.
- A diagnosis surfaces where knowing the fan-out across stages, not the per-stage count, is the differentiator.

### 9.2 Candidate ADR: OOM classification scope

- **Slug:** `pipeline-oom-classification`

**Problem.** `TransientFailureKind.MemoryExhausted` is one of the eight codes in §4.1, but the classifier is heuristic (exception type + log message). The question is whether OOM classification lives inside the transient-failure spec or becomes its own ADR that produces a separate signal.

**Options considered.**

- (a) Bounded to this spec. OOM is one of the eight transient kinds; classifier heuristic ships along with the rest.
- (b) Separate ADR for an OOM-only signal (`MemoryBudgetExceeded`, sensor-driven, cgroup/ORT-arena aware).
- (c) Split: OOM stays in the transient-failure spec; the broader memory-budget controller + classifier precision lands in its own ADR later.

**Trade-offs.** (a) keeps the spec scoped but lets a heuristic do work where a sensor would do better. (b) defers a real problem but forces a tiny OOM classifier into 9.2 + the existing sketch. (c) keeps the time to ship short and reserves the proper ADR for when signal improves.

**Recommendation.** (c). The existing `src/Trackdub.Application/Transcripts/DeviceFailureDegradationFactory.cs:36-43` already carries an OOM-oriented degradation record; the transient surface reuses the same boundary today. Promote when memory sensors become available.

**Promotion criteria.** Promote when any of the following is true:
- A concrete memory sensor (cgroup limits on Linux, ORT memory-arena caps, Avalonia render-frame budget tracking) is added and reports per-stage usage.
- OOM-classified faults start surfacing in user-visible UI (e.g. a banner on the pipeline panel) and need a richer payload than `TransientFailureKind.MemoryExhausted` can carry.
- A second OOM source (e.g. GPU memory on DirectML/CUDA/Metal) needs distinction from CPU OOM.

### 9.3 Candidate ADR: public diagnostics-bundle redaction

- **Slug:** `pipeline-bundle-redaction-transient`

**Problem.** The new `DiagnosticsBundle.transient` section will carry `Detail`, `Context` (free-form), and `StageName`. If exported for public sharing (model marketplace debug dumps, hosted support), some of that text could leak absolute paths, exception messages with file content, or system identifiers. The question is whether the new section needs its own redaction rules or inherits.

**Evidence (current state).**

- `src/Trackdub.Contracts/Diagnostics/UserProfilePathRedactor.cs:7-93` defines the existing redaction primitive (user-profile path masking).
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs:69-90, 116, 311-312` runs `RedactPaths` over each JSON artefact and byte content before writing the bundle.
- `src/Trackdub.App.Avalonia/Services/FailureDiagnosticsFormatter.cs:7-27` wraps `RedactUserProfilePaths` around `exception.Message` for the export-failed UI copy.

**Options considered.**

- (a) Inherit existing redaction. The new transient JSON is fed through `RedactPaths` like every other section; no new rules.
- (b) Add an extra redaction layer tailored to `PipelineTransientFault.Context` (paths, exception types, model ids).
- (c) Add an explicit `--share-safe` flag that runs the bundle through a more aggressive redaction pass before export.

**Trade-offs.** (a) is consistent and free; the new section ships with the same privacy posture. (b) reduces leakage of stage-specific identifiers but doubles the maintenance surface for redaction rules. (c) defers the harder call (what counts as share-safe) until a closer is signed.

**Recommendation.** (a). The exporter already iterates per section; routing the new transient JSON through the same `RedactPaths` call is one-line. Promote when share-mode is signed and the privacy surface becomes product-facing.

**Promotion criteria.** Promote when one or more of the following is true:
- The diagnostics bundle gains a real share-mode (community uploads, hosted support tunnel).
- A field is observed in the wild that escapes the current `UserProfilePathRedactor` (e.g. machine-id, GPU UUID, model manifest path).
- Compliance asks for a per-section redaction configuration rather than the existing global pass.

### 9.4 Candidate ADR: in-process Observable + upstream telemetry

> **Promotion:** see [`docs/adr/ADR-0015-pipeline-transient-telemetry.md`](../adr/ADR-0015-pipeline-transient-telemetry.md). Recommendation (a) "in-process only on on-prem tiers" was promoted on 2026-07-23 to ADR-0015; option (c) deferred to a future cloud-tier ADR.

- **Slug:** `pipeline-transient-telemetry`

Earlier drafts assumed a cloud OpenTelemetry/billing surface. Those paths were not current implementation evidence; the new AWS CPU job API does not implement that surface.

**Options considered.**

- (a) None. Observable stays in-process; per-tier consumers (Worker metrics, Avalonia VM, headless SDK) read directly.
- (b) Add an adapter interface `IPipelineTransientFaultExporter` plus one OTel implementation behind a feature flag.
- (c) No integration on the on-prem tiers; centralize in the cloud tier (API) by serializing transient-fault shipments into the existing trackdub-telemetry pipeline.

**Trade-offs.** (a) is the cheapest and matches the existing on-prem posture. (b) invites new infra on a desktop product, contradicts `AGENTS.md` §Model governance non-negotiables (“no new external telemetry surveillance on the end-user runtime path”). (c) preserves the on-prem posture and re-uses what already exists; fault shipments into the cloud tier are opt-in per project + per Tier.

**Recommendation.** (a) for the on-prem tiers shipped by this spec. Centralization, when it becomes a customer ask, belongs to (c) — the Cloud API / hosted Trackdub variant — and to a separate ADR.

**Promotion criteria.** Promote when one or more of the following is true:
- Trackdub.Cloud or a hosted customer SLI/SLO asks for per-stage transient-fault telemetry.
- A repeat user-facing incident traces to a failure pattern that surface logging alone cannot correlate.
- The Cloud tier upgrades to consume `PipelineTransientFault` directly and warrants a dedicated bridge.

## 10. References

- `docs/AGENTS.md` §Quality gates and §Verification ladder.
- `docs/architecture/P0-pipeline-audit-2026-06-01.md` — earlier pipeline failure-mode survey.
- `docs/BACKLOG.md` P0-1 closure 2026-06-10 — current "honest per-stage states" acceptance evidence.
- `src/Trackdub.Application/Dubbing/DubbingPipelineEngine.cs:1314` — existing `IsBenignSkipReasonCode`.
- `src/Trackdub.Domain/StageRuns/StageSkipReasonCodes.cs:8-29` — existing reason codes; this spec extends not replaces.
- `src/Trackdub.Application/Transcripts/StageRunHygiene.cs:13-58` — reconciliation precedent for stale-Running recovery (analog design).
- `src/Trackdub.Inference/Pool/InferenceRetryPolicy.cs:70` — ORT error-code paper-trail; this spec promotes to typed event.
- `src/Trackdub.App.Avalonia/Services/OperationRunner.cs:30-69` — lane semantics; spec extends.
- `src/Trackdub.Infrastructure/Diagnostics/DiagnosticsBundleExporter.cs:69-90, 116, 311-312` — section list to extend.
- `src/Trackdub.Worker/WorkerMetrics.cs:20-86` — existing `WorkerMetrics` pattern (used as template).
- `src/Trackdub.Contracts/Diagnostics/UserProfilePathRedactor.cs:7-93` — redaction primitive used by the bundle exporter (§9.3 evidence).
- `src/Trackdub.Api/Program.cs:47-49`, `src/Trackdub.Api/Observability/DubbingMetrics.cs:6`, `src/Trackdub.Api/Billing/Services/UsageMeter.cs:12` — historical assumed OTel surface, not shipped by the new AWS job API (§9.4 evidence).
- `MILESTONE.md` §Current non-negotiables — "no fake readiness / no silent failures / no silent degradation" gate.
- [`docs/adr/ADR-0015-pipeline-transient-telemetry.md`](../adr/ADR-0015-pipeline-transient-telemetry.md) — §9.4 (a) promotion: in-process `Observable` only on on-prem tiers (App/SDK/Worker/CLI); no new upstream sink. Cross-link closing the spec → ADR loop.

## 11. Validation ladder (per ADR candidate + main spec body)

Future code PRs implementing this spec, or any one ADR candidate, must satisfy the test surfaces named below. Each subsection lists required test files (suggested relative path under `tests/`), required test method names (suggested; agent may rename but must keep the same intent), required fixtures, required `dotnet test` filter, and required build TFM coverage. Conventions follow `AGENTS.md` §Verification ladder.

### 11.1 Cross-project build + test gates (apply to every change)

- `dotnet build Trackdub.sln -m:1 -p:Platform=x64` clean. `TreatWarningsAsErrors=true` is set globally per `Directory.Build.props`.
- `dotnet format --verify-no-changes` clean for every project in the diff (per `AGENTS.md` §Quality).
- Unit tests run via `dotnet test tests/<Project> --no-restore -m:1 --filter "<FullyQualifiedName>"`.
- Headless smoke runs via `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~HeadlessPipelineSmoke" $env:TRACKDUB_SMOKE_TIMEOUT_SECONDS=600` (only when a fixture + downloaded models exist on the agent).
- `dotnet test Trackdub.sln --configuration Release --no-build` is the final CI-equivalent gate. Failure there blocks the PR.

### 11.2 Main spec body — `TransientFailureKind` + bus + retry helper (§4.1–4.4)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/TransientFailureKindTests.cs`:
  - `IsTransient_returns_true_for_OperationCanceledException`
  - `IsTransient_returns_true_for_IOException_with_share_violation_hresult`
  - `IsTransient_returns_true_for_SqliteException_busy (5)`
  - `IsTransient_returns_true_for_OnnxRuntimeException_with_known_code`
  - `IsTransient_returns_true_for_HttpRequestException_5xx`
  - `IsTransient_returns_true_for_OutOfMemoryException`
  - `IsTransient_returns_false_for_ArgumentException`
  - `IsTransient_returns_false_for_NullReferenceException`
- New tests `tests/Trackdub.Application.Tests/Pipeline/PipelineTransientFaultBusTests.cs`:
  - `Publish_records_fault_in_snapshot`
  - `Publish_caps_snapshot_at_50_overflow_drops_oldest`
  - `Stream_yields_faults_in_arrival_order`
  - `UserCancellation_publishes_even_after_parent_CancellationToken_fires`
- New tests `tests/Trackdub.Application.Tests/Pipeline/StageRunWithTransientRetryTests.cs` (or co-located under `tests/Trackdub.Application.Tests/Pipeline/`):
  - `RunStageWithTransientRetry_succeeds_after_two_attempts_publishes_two_faults`
  - `RunStageWithTransientRetry_exhausts_three_attempts_rethrows`
  - `RunStageWithTransientRetry_UserCancellation_writes_Canceled_row_before_rethrow`
  - `RunStageWithTransientRetry_bubbles_non_transient_exception_unchanged`
- Fixtures: a deterministic `FakeTransientStage` (defined under `tests/Trackdub.TestDoubles/FakeTransientStage.cs` per `AGENTS.md` §Test doubles policy) that throws a controllable exception by attempt number, plus an `OperationCanceledExceptionSource` for cancel tests. Both injected through constructor parameters so production code stays untouched.
- `dotnet test tests/Trackdub.Domain.Tests --no-restore -m:1 --filter "FullyQualifiedName~TransientFailureKind"` must be 100% green.
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` must cover `TransientFaultBus` + `StageRunWithTransientRetry` + any orchestration chokepoint tests.
- TFM coverage: tests live in projects whose `<TargetFramework>` does not include `net10.0-windows10.0.19041.0`. Verify via `dotnet build tests/Trackdub.Application.Tests --no-restore`.

### 11.3 Reader surfaces (§4.5)

- New tests `tests/Trackdub.Infrastructure.Tests/Diagnostics/TransientFaultSummaryTests.cs`:
  - `MostRecent_caps_at_20_with_arrival_order`
  - `Total_matches_sum_of_countsByKind`
  - `Serialize_omits_empty_contexts`
- New tests `tests/Trackdub.Sdk.Tests/RunManifestTransientSectionTests.cs`:
  - `RunManifest_serializes_transient_section_with_integer_keys_for_kinds`
  - `RunManifest_transient_section_survives_roundtrip_through_trackdub_dub_engine`
- New tests `tests/Trackdub.Worker.Tests/WorkerTransientCounterTests.cs`:
  - `WorkerMetrics_transient_total_increments_per_fault`
  - `WorkerMetrics_transient_total_emits_to_existing_logger_path`
- New tests `tests/Trackdub.Cli.Tests/DoctorExplainTransientTests.cs`:
  - `DoctorHandler_explain_transient_user_cancellation_prints_remediation`
  - `DoctorHandler_explain_transient_directory_lock_prints_remediation`
  - `DoctorHandler_explain_transient_oom_prints_known_caveat`
- New tests `tests/Trackdub.App.Avalonia.Tests/ViewModels/PipelineRunViewModelTransientTests.cs` (Windows TFM):
  - `ApplyTransientFault_updates_LastTransientText_to_latest_kind`
  - `ApplyTransientFault_keeps_latest_when_count_exceeds_overlay_threshold`
  - `ApplyTransientFault_resets_text_when_run_succeeds`
- Fixtures: a `FakeRunManifestWriter` that captures the serialized JSON, an `InMemoryWorkerMetrics` that implements the counter-emit path, an Avalonia-friendly `FaultPublisher` mock.
- `dotnet test tests/Trackdub.Sdk.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` covers Sdk side.
- `dotnet test tests/Trackdub.Worker.Tests --no-restore -m:1 --filter "FullyQualifiedName~PipelineTransient"` covers Worker side.
- Avalonia UI tests may stay on `net10.0-windows10.0.19041.0` per `AGENTS.md` §Avalonia UI verification rule.

### 11.4 ADR-CAND `pipeline-transient-aggregation` (§9.1)

- New tests `tests/Trackdub.Application.Tests/Pipeline/PipelineTransientFaultBusSnapshotPerRunTests.cs`:
  - `SnapshotPerRun_returns_only_faults_with_matching_projectId`
  - `SnapshotPerRun_groups_by_stage_then_kind_in_arrival_order`
- Optional integration test composes the existing `RunStageWithTransientRetry` fixture and asserts the rollup matches.
- Command: `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~SnapshotPerRun"`.
- Promotion gate covered when this section is exercised; spec holds the test surface as a stub so the candidate can ship without further discovery.

### 11.5 ADR-CAND `pipeline-oom-classification` (§9.2)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/OomClassifierTests.cs` (only land if the candidate is promoted):
  - `Heap_ratio_over_threshold_returns_MemoryExhausted_kind`
  - `Cpu_memory_pressure_score_above_band_returns_MemoryExhausted_kind`
  - `Gpu_runtime_memory_event_maps_to_distinct_oom_subkind`
- These tests are stubs in this spec; they only ship together with the candidate. Before promotion, no test code is required.
- Promotion gate: each test name above is the surface the candidate needs to satisfy.

### 11.6 ADR-CAND `pipeline-bundle-redaction-transient` (§9.3)

- New tests `tests/Trackdub.Infrastructure.Tests/Diagnostics/TransientSectionRedactionTests.cs` (only land if the candidate is promoted):
  - `RedactPaths_passes_through_transient_section_string`
  - `RedactPaths_does_not_mutate_unredacted_fields`
  - `RedactPaths_handles_transient_section_with_no_user_profile_paths`
- These tests are stubs in this spec; they only ship together with the candidate. Before promotion, no test code is required.
- Promotion gate ensures the inheritance pattern (a) holds against regression.

### 11.7 ADR-CAND `pipeline-transient-telemetry` (§9.4)

- No tests added in this spec because the recommendation is (a), in-process only.
- Promotion gate: introduction of an opt-in exporter requires `tests/Trackdub.Api.Tests/Observability/TransientFaultExporterTests.cs` (or analog) with two surface tests:
  - `Exporter_publishes_to_existing_dubbing_metrics_counter`
  - `Exporter_does_not_initialize_when_feature_flag_disabled`
- These tests only ship when the candidate is promoted; spec keeps the surface documented to avoid re-discovery.

### 11.8 Cross-platform gates (§7)

- Avalonia UI tier builds + UI tests run on `net10.0-windows10.0.19041.0` per `AGENTS.md` TFM rule. The transient type + bus + retry helper are TFM-agnostic and live in `Trackdub.Domain` + `Trackdub.Application`.
- The classifier at `src/Trackdub.Domain/Pipeline/TransientFailureKind.IsTransient(Exception)` is platform-gated via `#if WINDOWS / #elif LINUX / #elif MACOS`. Each path has at least one unit test:
  - `IsTransient_Windows_share_violation_hresult_returns_true`
  - `IsTransient_Posix_EAGAIN_returns_true`
  - `IsTransient_Posix_EDEADLK_returns_true`
- `dotnet build Trackdub.Avalonia.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Cloud.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Inference.slnf -m:1 -p:Platform=x64` and `dotnet build Trackdub.Sdk.slnf -m:1 -p:Platform=x64` must stay clean for every change regardless of which lane is touched (per `AGENTS.md` CI gate).
- Headless smoke (`tests/Trackdub.Sdk.Tests/HeadlessPipelineSmoke`) runs the full spec under stub providers so the `RunManifest.transient` shape is observable end-to-end. Guarded by `SmokeTestFactAttribute`; skips cleanly when the smoke fixture or downloaded models are absent.

### 11.9 `StageRetryBudget` domain primitive (§4.4 retry-budget extraction)

- New tests `tests/Trackdub.Domain.Tests/Pipeline/StageRetryBudgetTests.cs`:
  - `Default_constants_match_legacy_hardcoded_values`
  - `BackoffFor_doubles_each_attempt_until_attempt_cap` (theory)
  - `BackoffFor_caps_at_MaxBackoffMs_for_very_high_attempts`
  - `BackoffFor_clamps_calculated_value_when_MaxBackoffMs_set_below_default`
  - `Constructor_throws_for_invalid_inputs` (theory)
  - `BackoffFor_throws_when_attempt_less_than_one`
- Updated tests `tests/Trackdub.Application.Tests/StageRunHelperTests.cs`: existing retry-test sites inject a `StageRetryBudget` instead of the removed inline `StageRunHelper.TransientFailureRetryOptions`; new fact `RunStageWithTransientRetry_aborts_after_one_attempt_when_budget_max_is_one` covers a budget narrower than `Default`.
- `dotnet test tests/Trackdub.Domain.Tests --no-restore -m:1 --filter "FullyQualifiedName~StageRetryBudget"` must be 100% green.
- `dotnet test tests/Trackdub.Application.Tests --no-restore -m:1 --filter "FullyQualifiedName~RunStageWithTransientRetry"` must cover both the `Default`-budget path and the injected-budget path.
- `StageRetryBudget` lives in `Trackdub.Domain` (no dependencies), so future per-stage tuning can thread a custom budget through `RunStageWithTransientRetryAsync` without forking the `StageRunHelper` chokepoint.

# Premade HF pack variants

Publisher-hosted ONNX variants let low-spec machines skip local Olive. Trackdub pulls the variant declared in each starter pack `runtime_defaults` row during **pack download**, not only at apply/runtime.

## Product behavior

1. Hardware profiler resolves `cpu_safe`, `balanced_gpu`, or `turbo_gpu`.
2. `StarterPackDownloadService` maps each spine model to `runtime_defaults[profile].variant`.
3. `ModelDownloadOrchestrator.DownloadAsync(modelId, variantAlias)` pulls base `download_files` plus that variant's files from Hugging Face (or `download_file_sources`).

Apply still writes the same variant overrides to settings; download and apply stay aligned.

## When to publish your own HF repos

Use a **Babelworks** or **tonythethompson** repo when:

- Upstream has no portable quant for the EP you want (common for custom Olive outputs).
- You want a single curated DirectML int4 bundle per model for balanced tier.
- You need smaller CPU packages than upstream ships.

Do **not** premake and upload:

- TensorRT / TRT-RTX engines (GPU-specific).
- Per-machine Olive outputs unless you version by SKU (high maintenance).

## Repo naming convention

```
tonythethompson/trackdub-{short-name}-{variant}
```

Examples (published):

- [tonythethompson/trackdub-silero-vad-int8](https://huggingface.co/tonythethompson/trackdub-silero-vad-int8)
- [tonythethompson/trackdub-silero-vad-fp16](https://huggingface.co/tonythethompson/trackdub-silero-vad-fp16)
- [tonythethompson/trackdub-kokoro-q8f16](https://huggingface.co/tonythethompson/trackdub-kokoro-q8f16)
- [tonythethompson/trackdub-kokoro-fp16](https://huggingface.co/tonythethompson/trackdub-kokoro-fp16)
- [tonythethompson/trackdub-phi-4-mini-cpu-int4](https://huggingface.co/tonythethompson/trackdub-phi-4-mini-cpu-int4)
- [tonythethompson/trackdub-phi-4-mini-gpu-int4](https://huggingface.co/tonythethompson/trackdub-phi-4-mini-gpu-int4)

Keep the same relative paths as the upstream manifest variant (`onnx/model_int8.onnx`, etc.) so manifest `download_file_sources` overrides stay minimal.

## Publishing workflow

1. Run Olive (or copy upstream quant) into a clean output folder matching manifest paths.
2. Verify commercial license and compute SHA-256 for each file.
3. Upload with `tools/models/Publish-TrackdubPackVariant.ps1`.
4. Add or update `bundled-models.manifest.json`:
   - Either add `download_file_sources` for specific paths, or
   - Add a new `model_id` pointing at your repo if it is a full mirror.
   - Record SHA-256 in `download_file_hashes` for each mirrored path (see `tools/ci/hashes-*-premade-variants.json` for Silero, Kokoro, Phi).
5. Run manifest validation tests and `dotnet test tests/Trackdub.Composition.Tests --filter StarterPack`.

## Manual download smoke

After publishing mirrors and updating `download_file_sources`, verify HF resolve URLs:

```powershell
# Full: every mirror URL in manifest (includes large Phi ONNX weights)
.\tools\models\Smoke-PremadePackVariantDownloads.ps1

# Quick: one reachability probe per mirror repo (HEAD or 1-byte range; no full ONNX download)
.\tools\models\Smoke-PremadePackVariantDownloads.ps1 -Quick

# Or directly:
python tools/ci/smoke-premade-pack-variants.py --quick
python tools/ci/verify-manifest-hashes.py --model-id microsoft/Phi-4-mini-instruct-onnx
```

## Bundled CI scope (same branch)

This branch also restores self-hosted OpenCode/Cursor review workflows (`.github/workflows/opencode-review.yml`, `cursor-code-review.yml`). That automation is repo-wide PR infra, not part of premade variant download behavior. See [docs/GITHUB_ACTIONS.md](../GITHUB_ACTIONS.md).

## Manifest gates

- `commercial_use_verified` must stay true for shipping models.
- Update `THIRD_PARTY_NOTICES.md` when attribution is required.
- Never mark Olive-local variants as HF-redistributable unless files are actually on HF.

# Service Blueprint — First Dub End-to-End

**Scenario:** A first-time user opens Trackdub, imports one local video, and produces + exports a dubbed result.
**State:** As-is (current build) with To-be (Δ) deltas side by side.
**Method:** Lanes grounded in real code (handlers, services, manifest), not aspiration. Component names are clickable anchors for coordination.

> **Read this as a system, not a feature list.** The payoff is where lanes *disconnect* — a frontstage promise the backstage can't keep, a long backstage stretch with no user touchpoint, or a single support dependency that takes a whole phase down. Those are collected in the [Gap Register](#gap-register); the To-be column is the fix for each.

---

## 0. Lanes & lines (legend)

| Lane | Trackdub meaning |
|---|---|
| **Physical evidence** | Screens, dialogs, waveform/timeline, the exported MP4 + subtitles |
| **User actions** | What the user does in the shell |
| **Frontstage** *(visible)* | App responses the user sees: progress, modal dialogs, candidate auditions |
| **Backstage** *(invisible)* | FFmpeg + ONNX work, artifact writes, persistence |
| **Support** | Model manifest, models on disk, execution providers, FFmpeg binaries, SQLite, **optional cloud APIs** |

**— line of interaction —** between User actions and Frontstage
**— line of visibility —** between Frontstage and Backstage
**— line of internal interaction —** between Backstage and Support

---

## 1. One-glance swimlane matrix (as-is)

Phases collapse the 7 canonical pipeline stages into user-facing steps. Stage internals live in Backstage under **Transcribe & Translate** — they are not separate user steps (altitude discipline). *Verified against `TrackdubDubbingEngine.DefaultStageOrder` — see [Validation log](#validation-log--backstage--support-lanes-verified-against-source).*

| Lane ↓ / Phase → | 1 · Launch | 2 · Import | 3 · Configure run | 4 · Transcribe & translate | 5 · Review & assign voices | 6 · Preview mix | 7 · Export |
|---|---|---|---|---|---|---|---|
| **Physical evidence** | Empty 5-panel shell, titlebar | MediaBin thumbnail, waveform | RunConfig, PipelineStages list, source-lang picker | Per-stage progress rows; readiness/consent modals | Segment list/detail, speaker cards, TTS candidate selector, glossary | Mini preview player, timeline, transport bar | ExportMix dialog → MP4 + .srt + manifest on disk |
| **User actions** | Launch app, (open/new project) | Drag-drop / pick file | Pick source+target lang, toggle stages, pick model tier | Click **Run**, then *wait* | Edit text, rename/merge speakers, assign voice per speaker, audition candidates | Scrub, play, judge sync/mix | Choose format, click **Export** |
| **Frontstage** *(visible)* | Shell composition, last-project restore | Probe result, waveform render, duration badge | Stage enable/skip reasons, EP/model readiness badges | Stage status (running/skipped/failed), **modal setup + license + clone-consent dialogs mid-run** | Inline edits commit, candidate playback, fallback-voice dialog | Range render + playback | Progress, success toast, reveal-in-folder |
| **Backstage** *(invisible)* | DI graph build, settings load | `FfmpegMediaProbe`, `FfmpegAudioExtractionService`, waveform summary, ingest write | Stage planning, device-affinity resolve *(pre-flight is at run start, not here)* | Separation→VAD→Diarization→ASR→Translation (canonical order); ONNX sessions; atomic artifact writes + run-level resume | `StartTtsStageHandler`, `GenerateCandidatesHandler`, TTS synth, **WSOLA/ffmpeg time-stretch to fit takes to duration** | `PreviewMixWorkflow` + `PreviewRangeRenderer`: gains/ducking/room-tone/pan/downmix | `MixPlanBuilder` → full mix → `FfmpegMuxer`, `SubtitleExportService` (takes already time-fitted at TTS) |
| **Support** | SQLite, studio settings, app log | FFmpeg binaries (auto-download), temp dirs | Bundled manifest, model registry, HW profiler | **models/ on disk + HF download**, checksum verify, **EP setup (TRT-RTX plugin/DML/CUDA/WinML catalog)**, Olive optimize, license catalog, **opt. cloud ASR/MT** | kokoro voices, **opt. cloud TTS (ElevenLabs/OpenAI)**, clone-consent | FFmpeg encoders, room-tone impulse | FFmpeg mux/encoder selection, export manifest store |

---

## 2. Per-phase detail (as-is → to-be Δ)

Each phase: 5-lane table. **As-is** = what the build does today. **To-be Δ** = the change that closes the gap surfaced in that lane.

### Phase 1 · Launch
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Empty 5-panel shell; no welcome/onboarding screen (grep found none) | First-run welcome surface: what Trackdub does, privacy/local-first statement, "try sample clip" |
| User actions | Launch; optionally open/new project | Same; guided first path instead of blank canvas |
| Frontstage | Shell composition, last-project restore | Add first-run state branch + privacy/cloud disclosure |
| Backstage | DI graph build (`CompositionRoot`), settings load | Detect "no projects yet" → route to onboarding |
| Support | SQLite, `JsonStudioSettingsService`, app log | Persist `firstRunCompleted`; ship a tiny bundled sample media |

> **Gap G1 — no onboarding.** First dub starts on a blank shell; the user must already know the flow.

### Phase 2 · Import
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | MediaBin item, waveform, duration badge | Same + codec/encoder compatibility chip up front |
| User actions | Drag-drop or pick file | Same |
| Frontstage | Probe result + waveform | Surface probe warnings (unsupported codec, no audio track) **before** Run, not at stage failure |
| Backstage | `FfmpegMediaProbe`, `FfmpegAudioExtractionService`, `WaveformSummaryGenerator`, `ProjectMediaIngestService` | Validate audio presence/encoder support during ingest; persist verdict |
| Support | **FFmpeg binaries — auto-download on demand** | Pre-flight FFmpeg health (`FfmpegHealthCheck`) at import, not first FFmpeg call |

> **Gap G2 — FFmpeg is a silent single point of failure.** If `FfmpegAutoDownloader` hasn't resolved a binary, failure surfaces deep in a backstage stage, far from the import action that caused it.

### Phase 3 · Configure run
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | RunConfig, PipelineStages, stage-options, source-lang picker | Add target-language + cloud-vs-local toggle with explicit privacy note per stage |
| User actions | Pick source+target lang, toggle stages, pick tier | Same + opt into cloud per stage knowingly |
| Frontstage | Skip/enable reasons, readiness badges | Show **which engine each stage will use (local model vs named cloud provider)** before Run |
| Backstage | Stage planning, `DeviceAffinitySettings` — **model pre-flight is deferred to run start, not done here** | Run pre-flight + engine resolution **here**, before the user commits to Run |
| Support | Manifest, model registry, HW profiler | `CloudAwareTranslationEngine` / `CloudAwareTtsEngine` selection made visible, not implicit |

> **Gap G3 — engine selection is invisible.** `CloudAware*` wrappers silently choose local vs cloud at run. The user can't see, before pressing Run, that segment text/audio may egress to DeepL/OpenAI/Gemini/ElevenLabs.

### Phase 4 · Transcribe & translate  *(the long backstage stretch)*
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Per-stage progress rows; **modal** setup/license/consent dialogs appear mid-run | Inline per-stage ETA + non-blocking readiness resolved **before** Run |
| User actions | Click **Run**, then wait through 7 stages | One Run; readiness prompts front-loaded, not interrupting |
| Frontstage | Stage status + structured skip/fail reasons; `RuntimeModelSetupDecisionDialog`, `ModelNotReadyDialog`, `DiarizationModelSetupDecisionDialog`, `EpVendorLicenseDialog` | Move readiness/consent to Configure; during Run show progress + ETA + cancel only |
| Backstage | Canonical order (`DefaultStageOrder`): Separation (spleeter/mrx) → VAD (silero) → Diarization (sortformer, **before** ASR) → ASR (whisper/qwen3) → Translation (opus-mt/madlad/phi). Prep/enhance + qwen2.5 refine are sub-steps, **not** canonical stages. **VAD & ASR are blocking prerequisites** (failure → later stages `PREREQUISITE_FAILED`; others → PartialSuccess). Atomic writes + run-level resume skip stages matching the `ExecutionSnapshot` | Emit per-stage progress %/ETA; same prerequisite + resume guarantees |
| Support | models/ + HF download, **checksum verify**, EP install + Olive optimize, license catalog; **opt. cloud ASR/MT (BYO key, routed by model alias)**. `RuntimePlannerPreFlightChecker` plans model+EP per stage; **auto-downloadable VAD/ASR/Diar models are fetched mid-stage, not before Run** | Pre-stage all downloads/optimization in Configure; cloud egress logged + consented |

> **Gap G4 — one action, then a long blind wait.** Run is a single user touchpoint followed by 7 sequential backstage stages. Progress exists per stage but **no ETA**; first-run model download + Olive optimization + EP install can run for minutes inside this stretch.
> **Gap G5 — readiness is partly front-loaded, partly reactive (inconsistent).** `RuntimeModelSetupCoordinator.EnsureImportModelsAvailableAsync` runs the setup decision loop (Download / Import / Skip) at **import** — good, front-loaded. But model-tier, diarization, and voice decisions made *after* import surface their setup dialogs **mid-run**, and the headless SDK path throws instead of prompting. Only **Separation** is skippable (`IsOptionalRuntimeStage`). The gap is the inconsistency, not total absence — softened from v1 by the import-time front-loading.

### Phase 5 · Review & assign voices
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Segment list/detail, speaker cards, glossary panel, TTS candidate selector | Same + diff highlight of edited vs original segments |
| User actions | Edit text, rename/merge speakers, assign voice per speaker, audition candidates | Same |
| Frontstage | Inline edit commit, `FallbackVoiceGenerationDialog`, `VoiceCloneConsentDialog`, candidate playback | Surface confidence/low-quality segments first (`TranscriptConfidenceEvaluator`) |
| Backstage | `SegmentEditingService`, `SpeakerAssignmentService`, `StartTtsStageHandler`, `GenerateCandidatesHandler`, TTS synth + post-process. **Timing reconciliation lives here** — `TtsOrchestrationService` fits each take to its segment duration via WSOLA/ffmpeg time-stretch (`AudioTimeStretchService`, `WsolaPhonemeStretchService`) | Same; persist audition choices as durable artifacts |
| Support | kokoro voices; **opt. cloud TTS (ElevenLabs/OpenAI)**; clone-consent gate | Cloud TTS egress consented + logged like cloud MT |

> **Gap G6 — voice-clone consent exists, but cloud-TTS egress is a separate unflagged boundary.** `VoiceCloneConsentDialog` gates cloning; sending dialogue text to ElevenLabs/OpenAI for synthesis is a distinct privacy event that should be equally explicit.

### Phase 6 · Preview mix
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | Mini preview player, waveform timeline, transport bar | Same + A/B original-vs-dub toggle |
| User actions | Scrub, play a range, judge timing/mix | Same |
| Frontstage | `PreviewMixWorkflow` range render + playback (libmpv) | Faster incremental preview; show mix gain per track |
| Backstage | `PreviewRangeRenderer`: source/dubbed/**ducking** gain staging + optional room-tone timbre-polish (0.3s source pre-roll convolution, `RoomToneConvolver`) + optional pan-restore (original L/R RMS) + multichannel→stereo downmix. **Range-only** render, tracked as `StageNames.PreviewMix`. *Loudness norm is NOT here — separate `FfmpegLoudnessNormalizer` at extraction* | Cache rendered ranges |
| Support | FFmpeg encoders, libmpv natives, room-tone impulse | libmpv health checked at startup (see playback-native-layout.md) |

### Phase 7 · Export
| Lane | As-is | To-be Δ |
|---|---|---|
| Physical evidence | ExportMix dialog → MP4 + .srt + export manifest on disk | Same + export summary (engines used, cloud calls made, attribution) |
| User actions | Choose format, click Export | Same |
| Frontstage | Progress, success, reveal-in-folder | Show what shipped: which models/cloud providers + required attributions |
| Backstage | `MixPlanBuilder` → full-length mix (same DSP as preview) → `FfmpegMuxer`, `SubtitleExportService`, `ExportManifestModels`. **Timing reconciliation already happened at TTS — not here** | Emit attribution/provenance into manifest |
| Support | FFmpeg mux + encoder selection, mix-plan store | Encoder capability pre-checked (`FfmpegVideoEncoderCapabilityService`) before Export, not at mux |

> **Gap G7 — no provenance at the finish line.** Several bundled models require attribution (sortformer, spleeter, mrx, kokoro) and cloud providers have their own terms. The export doesn't tell the user what was used or what attribution they owe.

---

## Gap Register

Ranked by how badly a lane disconnect hurts the first-dub experience. Each maps to the To-be Δ above.

| # | Gap | Lanes that disconnect | Severity | Fix (to-be) |
|---|---|---|---|---|
| **G5** | Readiness **inconsistent**: front-loaded at import, but post-import tier/diar/voice changes interrupt mid-run (headless throws) | Frontstage ↔ Support, across Phase 4 | **Med** *(was High; softened by import front-loading)* | Make import-time setup the single gate; re-validate at Configure when selections change |
| **G4** | **Run = one action then a long blind wait**; per-stage progress but no ETA; first-run downloads/optimize hide here | User actions ↔ Backstage, Phase 4 | **High** | Per-stage ETA; pre-stage downloads + Olive optimize before Run |
| **G3** | **Local-vs-cloud engine choice is invisible** before Run; data may egress unknowingly | User actions ↔ Support, Phase 3 | **High** | Per-stage engine badge (local model vs named cloud provider) at Configure |
| **G2** | **FFmpeg auto-download is a silent SPOF**; failure surfaces deep in a stage | Frontstage ↔ Support, Phase 2 | **Med** | FFmpeg health-check at import, not at first FFmpeg call |
| **G1** | **No onboarding**; first dub starts on a blank shell | Physical evidence ↔ User actions, Phase 1 | **Med** | First-run welcome + privacy statement + sample clip |
| **G6** | **Cloud-TTS egress** is an unflagged boundary distinct from clone consent | Frontstage ↔ Support, Phase 5 | **Med** | Consent + log cloud TTS egress like cloud MT |
| **G7** | **No provenance/attribution** in the exported result | Backstage ↔ Physical evidence, Phase 7 | **Low** | Export summary: engines used, cloud calls, attributions owed |

### Reading the blueprint (the four classic signals)

- **Frontstage promises the backstage can't keep:** G5 — the run *looks* like a single press, but the backstage demands setup decisions it surfaces as surprise modals.
- **Single support dependency takes a phase down:** G2 (FFmpeg), and libmpv for Preview — both are auto-resolved natives with no early health gate.
- **Long horizontal stretch with no user touchpoint:** Phase 4 — 7 sequential stages behind one Run; the user is waiting and (G4) under-informed about how long.
- **Cross-cutting support fragility made invisible:** G3/G6 — the `CloudAware*` wrappers route between local and cloud per stage with no visible boundary, even though that boundary is the whole local-first value proposition.

---

## Validation log — backstage & support lanes (verified against source)

Skill step 9: validate the invisible lanes with the team that owns them. Here the owner is the code. Each item was checked against the cited file. **✅ confirmed · ✏️ corrected · ➕ added (was missing in v1).**

### Pipeline orchestration & stage order
- ✏️ **Canonical run order is 7 stages, not 8.** `TrackdubDubbingEngine.DefaultStageOrder` (Sdk): **Separation → VAD → Diarization → ASR → Translation → TTS → Export**. Diarization is deliberately before ASR so speaker labels exist when `SpeakerAssignmentAndPersistenceStage` persists the transcript.
- ✏️ **Speech prep/enhancement, qwen2.5 text-refinement, lip-sync, and mixing are NOT canonical top-level stages.** Handlers exist (`SpeechAudioPreparationStageHandler`, `LipSyncStageHandler`, `MixPlanBuilder`) but are sub-steps / separate workflows, absent from `DefaultStageOrder`. Mixing folds into **Export** in the headless path; the app adds an interactive **Preview** layer (`PreviewMixWorkflow`) on top.
- ✅ Transcript sub-pipeline order confirmed: `TranscriptGenerationService` builds VAD → Diarization → ASR → SpeakerAssignment via `TranscriptPipelineBuilder.AddStage(...)`.
- ✅ Stages run **sequentially** (`TranscriptGenerationPipeline.ExecuteAsync` foreach; `CloudAwareTranslationEngine` is explicitly "not thread-safe, sequential pipeline use"). Confirms G4's long blind wait.
- ➕ **SDK vs App:** the 7-stage canonical order is the *headless* `TrackdubDubbingEngine` truth. The Avalonia first-dub interleaves user steps (review/voice/preview) between Translation and Export using the **same** `workspace` workflows (`RunTranscriptStageAsync`, `GenerateTranslationAsync`, `GenerateTtsForAllSpeakersAsync`, `ExportAsync`).

### Failure / skip / resume semantics  *(missing from v1)*
- ➕ **Prerequisite gating:** only **VAD and ASR** block downstream (`PrerequisiteStages`). Their failure → later stages `Skipped / PREREQUISITE_FAILED`. Separation/Diarization/Translation/TTS failing yields **PartialSuccess**, not total failure.
- ➕ **Run-level resume:** before each stage, `HasValidExistingArtifactsAsync` → `StageArtifactResumeEvaluator.CanResumeStage` skips stages whose artifacts match the current run's `ExecutionSnapshot` (`EXISTING_ARTIFACTS_VALID`). This — plus the immutable `ExecutionSnapshot` captured at run start — is the real "preserve artifacts" guarantee.
- ✏️ **`ArtifactWriteTransaction` is narrower than v1 stated:** it is an atomic temp→commit wrapper — an uncommitted (failed) write deletes the temp file and never touches `FinalPath`. Preservation-on-failure is a *consequence* of atomic commit, not an explicit preserve step.
- ✅ Structured reasons confirmed: `StageOutcome.ReasonCode` (CANCELLED / PREREQUISITE_FAILED / STAGE_FAILED / EXISTING_ARTIFACTS_VALID) + `PipelineDegradationRecord` (code, detail, recommended action).

### Readiness / pre-flight  *(repositioned — reinforces G5)*
- ✏️ **Pre-flight runs inside the run, after the user presses Run.** `RunPreFlightChecksAsync` executes once at the top of `ExecuteAsync`, before the stage loop — **not** a Configure-time gate. (v1 placed `PipelinePreFlightChecker` in Phase 3; corrected.)
- ➕ **Auto-downloadable VAD/ASR/Diarization models do NOT fail pre-flight — they are provisioned *mid-stage*** (`TrackdubDubbingEngine.RunPreFlightChecksAsync`, the `CanAutoDownload && stageProvisionedDuringExecution` branch). A first run can therefore stall on model downloads inside a stage with no earlier warning. Direct evidence for G4.
- ✅ `RuntimePlannerPreFlightChecker` plans model + execution provider per stage; throws `RequiredModelNotAvailableException` when the plan is `Blocked` (no compatible model/EP) or `DownloadRequired`.

### Cloud routing  *(sharpens G3)*
- ✅/✏️ **Local-vs-cloud is chosen per stage purely by `PreferredModelAlias`** (the model picker). `CloudAwareTranslationEngine` routes to DeepL/OpenAI/Gemini when the alias matches, else local; `CloudAwareTtsEngine` mirrors this for ElevenLabs/OpenAI. There is **no separate cloud-egress consent**, and pre-flight even *skips* the local-model check for cloud aliases (`ShouldSkipModelPreFlight` → DeepL). The egress boundary is invisible and rides on a dropdown.

### FFmpeg / native support  *(confirms G2)*
- ✅ `FfmpegToolResolver` resolves in order: explicit path → `TRACKDUB_FFMPEG_PATH` → PATH → common roots (winget/choco/Program Files) → installer payload → **auto-download (last resort, `allowAutoDownload=true`)**. If all fail it throws `InvalidOperationException` deep in the first media op. Resilient on dev machines; on a clean end-user box the whole pipeline hinges on that one auto-download — a real single point of failure.

### Round 2 — mixing / preview DSP, model-setup flow, cloud TTS

- ✅ **Cloud TTS mirrors translation routing.** `CloudAwareTtsEngine` routes by `request.Options.NormalizedPreferredModelAlias`: ElevenLabs / OpenAI / **Google** → cloud, else local. Cloud TTS is **3 providers** (v1 said 2). Same no-consent alias mechanism as G3 — confirms G6.
- ✏️ **Model setup is an interactive decision loop, partly front-loaded.** `RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync`: per request, check `GetRequiredModelStatusAsync`; if missing, loop on `callbacks.ResolveDecisionAsync` → **Cancel / Download / Import / SkipOptionalStage** (this callback *is* the modal dialog). `RuntimeModelSetupCoordinator.EnsureImportModelsAvailableAsync` runs it at **import**; per-stage `Ensure*Available` on demand. Only **Separation** is skippable (`IsOptionalRuntimeStage`). → refines G5 (now Med).
- ✏️ **Preview/mix DSP is richer and different from v1.** `PreviewRangeRenderer.RenderAsync`: source-gain + **ducking** (`FillDuckingGains` from `MixPlan.DuckingRegions`) + dubbed-speech-gain; per-clip **room-tone timbre-polish** via `RoomToneConvolver.TryApply(dryTake, 0.3s source pre-roll)` **gated by `ApplyTimbrePolish`**; optional **pan-restore** from original L/R RMS; full **multichannel→stereo downmix** (channel-mask aware, 5.1/7.1). It renders a **range only** and is a tracked `StageNames.PreviewMix` stage-run with atomic artifact commit + fingerprint.
- ✏️ **Loudness normalization is NOT in the mix renderer** — it's `FfmpegLoudnessNormalizer` (Media/Loudness), applied at audio extraction. v1 wrongly placed it in Preview backstage.
- ✏️ **Timing reconciliation is a TTS-stage concern, not Export.** `WsolaPhonemeStretchService` / `AudioTimeStretchService` are driven by `TtsOrchestrationService` + `StartTtsStageHandler` to fit each take to its segment duration. The renderer only **resamples** (sample-rate) and **time-places** takes (`MixTakeIntoOutput`) — no stretching. Export muxes the already-fitted mix. v1 mislabeled this as an Export step.
- ✅ Preview is a **first-class tracked stage** (`StageNames.PreviewMix`, `StageRunHelper` Start/Complete/Fail/Cancel), not a transient UI render.
- ⏳ **Not traced:** `ExportStageHandler` exact call graph (also references a stretch service — possible final-fit), and `RoomToneFallbackImpulse` usage when no pre-roll exists. Low risk; flagged for honesty.

### Net effect
Corrections **strengthen most gaps and honestly soften one.** Round 1: G2/G3/G4 each gained a code-cited mechanism; two backstage truths were missing (prerequisite gating, run-level resume). Round 2: **G5 softened High→Med** (import front-loads setup), **G6 confirmed** (3 cloud TTS providers, no consent), and the mix/timing lanes were corrected (loudness ≠ preview; WSOLA = TTS, not Export). Where the as-is tables and this log differ, **this log is authoritative.**

---

## Notes on accuracy / scope

- **Local-first holds by default.** Translation, ASR, TTS all have bundled local ONNX engines. The cloud tier (OpenAI / Gemini / DeepL / ElevenLabs) is **opt-in, BYO-key, and genuinely wired** via `CloudAware*` engines in `Trackdub.Composition` + `ApiKeyStore` / `EnvironmentCloudApiKeyProvider` — not a dead dialog. That makes the local↔cloud boundary a first-class blueprint concern, not a footnote.
- **The legacy `Trackdub.Application/Services/TranslationService` stub is gone**, removed along with the other unused legacy services (it only ever returned `[Translated: …]`). The live path is `TranslationOrchestrationService` (`Trackdub.Application.Transcripts`) → `CloudAwareTranslationLanguageRouter` (`Trackdub.Composition.Translation`) → local opus-mt/madlad/phi or cloud. Disambiguation rule for the future: live translation types live under `Transcripts`, so a same-named type under `Application/Services` is a stub — that folder now holds only `IUpdateService.cs`.
- **Scope held to first-dub.** First-run *model governance* (checksum/license/commercial gating, EP install, Olive optimization) is its own candidate blueprint — it appears here only as the Support lane of Phase 4, deliberately not expanded.
- **Co-create next:** validate the Backstage and Support lanes against the actual stage handlers with whoever owns the pipeline; backstage steps are the lane most often missing from a first draft.

# Starter packs v1 design

**Status:** Implementation-ready (approved 2026-06-12).  
**Source of truth:** `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json`  
**Consumers:** `Trackdub.Cli` (headless + TUI), `Trackdub.App.Avalonia` (Model Manager), `Trackdub.Sdk` (readiness).

## Goal

Ship three starter packs that make first-run model setup understandable and hardware-aware without creating a second model-selection system:

1. **Basic / Fast** — smallest usable dubbing spine.
2. **Balanced / Mid** — creator-ready spine with diarization and better ASR.
3. **Premium / Quality** — higher-quality ASR, voice cloning, and dedicated multilingual translation.

Starter packs must:

- List bundled commercial models to download for a usable dubbing spine.
- Declare runtime defaults per hardware profile: manifest **alias**, **variant alias**, and **execution provider**.
- Document optional Olive targets as recommendations only.
- Integrate with existing hardware profiler, `IRuntimePlanningPreferences`, `RuntimePlannerRankingStrategy`, EP smoke tests, and `RuntimePlanFallback`.

## Non-goals v1

- Do not auto-run Olive during pack download.
- Do not include non-commercial, experimental lane, or blocked models in starter-pack download sets.
- Do not include lip sync.
- Do not replace Model Manager stage grouping in Avalonia (tier bands are additive).
- Do not promise a language pair default such as English → Spanish.
- Do not co-download alternate ASR models with the primary ASR model.
- Do not use `Xenova/whisper-small` in pack authoring (not in bundled manifest for shipping ONNX path).

---

## Locked product decisions

| Area | Decision |
|---|---|
| Pack location | Bundled defaults ship beside the executable. User overrides: `{InstallDir}/StarterPacks/*.json`. `InstallDir` = `AppContext.BaseDirectory` (portable and installed builds). Not repo root. Not AppData. |
| Workflow | `download` = models only. `apply` = aliases, variants, EP defaults, tier preference. `optimize` = explicit post-download Olive. |
| Windows copy | Say **GPU-optimized**, not CUDA. Runtime uses TRT RTX EP ABI plugin (`trt-rtx`) or Windows ML/DirectML fallback (`directml`). |
| Linux copy | Say CUDA / TensorRT when smoke-tested and available. |
| Translation | Universal pivot models in packs. Opus-mt pair models are optional add-ons only. |
| Diarization | Balanced-required. Optional in Basic. |
| Balanced ASR | Primary profile: `openai/whisper-small`. Alternate profile `balanced-multilingual`: `tonythethompson/qwen3-asr-0.6b-onnx`. |
| Premium ASR | Primary profile: `openai/whisper-medium`. Alternate profile `premium-multilingual`: `tonythethompson/qwen3-asr-1.7b-onnx`. |
| Voice cloning | Premium with consent. Chatterbox family has `requires_user_consent: true`. |
| Lip sync | Excluded v1. `musetalk-v1-5` is blocked. |
| CPU-only fallback | Use Basic-fast behavior. `CpuSafe` must map to manifest tier `fast`. |

---

## Three-phase workflow

```text
models packs download <id> [--profile <profile-id>] [--yes]
models packs apply <id>   [--profile <profile-id>] [--hardware-profile <profile>] [--yes]
models optimize           [--pack <id>] [--yes]    # existing optimize entry; pack-scoped optional
```

Rules:

- `download` must not change aliases or run Olive.
- `apply` must validate model IDs, variants, EPs, and commercial policy before persisting settings.
- `optimize` is user-triggered; failure keeps the base model and reports fallback honestly.
- Premium `apply` with Chatterbox must require `--accept-voice-cloning-consent` or an interactive consent prompt (TUI/desktop).

---

## Tier mapping

| Starter pack | Pack `tier_preference` | Manifest `tier` values used for planner bias | Typical hardware profile |
|---|---|---|---|
| Basic / Fast | `fast` | `fast` | `cpu_safe` |
| Balanced / Mid | `balanced` | `balanced` | `balanced_gpu` |
| Premium / Quality | `quality` | `quality`, `accurate` | `turbo_gpu` |

Profiler quirk (existing): `HardwareQualityPreset.Turbo` maps to manifest tier string `"fast"` in `HardwarePresetRecommendation.ToModelTierPreference`. Do not rename in v1; starter packs set `tier_preference` explicitly on apply.

**Required fix before packs ship:** `CpuSafe` must map to `"fast"`, not `"balanced"` (`src/Trackdub.Domain/HardwareProfiler.cs` line ~108).

---

## Hardware profiles

| Profile | Detected when | Variant bias | EP bias (Windows) | EP bias (Linux) |
|---|---|---|---|---|
| `cpu_safe` | No GPU or profiler `CpuSafe` | int4/int8/q4 | `cpu` | `cpu` |
| `balanced_gpu` | GPU present, moderate VRAM | fp16 / gpu-int4 | `directml` | `cuda` when smoke-tested |
| `turbo_gpu` | High-end GPU + TRT RTX plugin or catalog EP smoke pass | fp16 / gpu-int4 | `trt-rtx` then `directml` | `tensorrt` / `cuda` when smoke-tested |

When `--hardware-profile` is omitted on `apply`, resolve from latest `IHardwareProfilerService` recommendation:

| `HardwareQualityPreset` | Pack hardware profile |
|---|---|
| `CpuSafe` | `cpu_safe` |
| `Balanced` | `balanced_gpu` |
| `Turbo`, `Quality` | `turbo_gpu` |

---

## Starter pack download sets

### Basic / Fast (`id: basic`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `onnx-community/whisper-tiny` | `asr` | yes | `whisper-tiny` |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | `tts` | yes | `kokoro-onnx` |
| `microsoft/Phi-4-mini-instruct-onnx` | `translation` | yes | `phi-4-mini` |

Optional: `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` (`sortformer-4spk`), `csukuangfj/sherpa-onnx-spleeter-2stems`.

### Balanced / Mid (`id: balanced`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `diarization` | yes | `sortformer-4spk` |
| `openai/whisper-small` | `asr` | yes (profile `default`) | `whisper-small-genai` |
| `tonythethompson/qwen3-asr-0.6b-onnx` | `asr` | yes (profile `balanced-multilingual`) | `qwen3-asr-0.6b` |
| `onnx-community/Kokoro-82M-v1.0-ONNX` | `tts` | yes | `kokoro-onnx` |
| `microsoft/Phi-4-mini-instruct-onnx` | `translation` | yes | `phi-4-mini` |

Optional: `csukuangfj/sherpa-onnx-spleeter-2stems`, `Rikorose/DeepFilterNet3`.

### Premium / Quality (`id: premium`)

| Model ID | Stage | Required | Manifest alias (apply) |
|---|---|---|---|
| `onnx-community/silero-vad` | `vad` | yes | `silero-vad` |
| `cgus/diar_streaming_sortformer_4spk-v2.1-onnx` | `diarization` | yes | `sortformer-4spk` |
| `openai/whisper-medium` | `asr` | yes (profile `default`) | `whisper-medium-genai` |
| `tonythethompson/qwen3-asr-1.7b-onnx` | `asr` | yes (profile `premium-multilingual`) | `qwen3-asr-1.7b` |
| `ResembleAI/chatterbox-turbo-ONNX` | `tts` | yes | `chatterbox-turbo-onnx` |
| `google/madlad400-3b-mt` | `translation` | yes | `madlad400` |

Optional: Qwen2.5 polish, Nemotron, Phi translation upgrades, spleeter, opus-mt pairs, alternate chatterbox clones.

**Honesty gate:** `sortformer` has `commercial_use_verified: false` in manifest today. Packs may list it; readiness UI must not claim commercial verification until manifest is updated.

---

## Translation strategy

Packs use **universal** pivot models. No `translation_pair` in pack defaults.

| Pack | Model | Alias on apply |
|---|---|---|
| Basic, Balanced | `microsoft/Phi-4-mini-instruct-onnx` | `phi-4-mini` |
| Premium | `google/madlad400-3b-mt` | `madlad400` |

`TranslationLanguageRouter` resolution order (when override is `Auto` and no explicit stage alias):

1. User-preferred alias (from `StageModelAliases` or pipeline selection).
2. Direct Opus route if that pair model is installed.
3. Madlad pivot.
4. Phi genai pivot.

Opus-mt models remain optional manifest entries for advanced `models packs add --pair en-es`. Not in starter download sets.

---

## Pack JSON schema

Files: `basic.json`, `balanced.json`, `premium.json` (bundled) plus optional user files in `{InstallDir}/StarterPacks/`.

```jsonc
{
  "schema_version": 1,
  "id": "basic",                        // stable slug
  "display_name": "Basic / Fast",
  "tier_preference": "fast",            // persisted to StudioSettings.ModelTierPreference
  "description": "Smallest dubbing spine: VAD, tiny ASR, Kokoro TTS, universal Phi translation.",
  "profiles": [                         // omit or single "default" for basic
    {
      "id": "default",
      "display_name": "Default",
      "asr_model_id": "onnx-community/whisper-tiny"   // only field that differs between profiles
    }
  ],
  "models": [
    {
      "model_id": "onnx-community/silero-vad",
      "stage": "vad",
      "required": true,
      "alias": "silero-vad",
      "runtime_defaults": {
        "cpu_safe":     { "variant": "int8",  "execution_provider": "cpu" },
        "balanced_gpu": { "variant": "fp16",  "execution_provider": "directml" },
        "turbo_gpu":    { "variant": "fp16",  "execution_provider": "trt-rtx" }
      },
      "olive": null
    }
  ],
  "translation": {
    "strategy": "universal",              // "pair" only for advanced add-on profiles
    "model_id": "microsoft/Phi-4-mini-instruct-onnx",
    "alias": "phi-4-mini"
  },
  "optional_models": [ "cgus/diar_streaming_sortformer_4spk-v2.1-onnx" ],
  "olive_auto_run": false
}
```

Validation (`StarterPackValidator`):

- `schema_version` must be `1`.
- Every `model_id` exists in bundled manifest with `commercial_allowed: true` and no `lane: experimental`.
- Every `variant` exists on that manifest entry's `variants[]` (or `"default"` when the model has no variants).
- Every `execution_provider` is a known `ExecutionProviderKind` string (`cpu`, `directml`, `cuda`, `trt-rtx`, `tensorrt`, `migraphx`).
- Exactly one primary ASR per profile (profile picks `asr_model_id`; other stages shared).
- `translation.strategy` is `universal` for shipping packs unless `id` ends with `-pair-addon` (advanced user pack convention).
- User pack with same `id` as bundled pack overrides bundled (load order: bundled first, user dir wins on duplicate `id`).

---

## Prerequisite code changes (blockers)

These are required before `apply` can work end-to-end in CLI and desktop:

### 1. Persist per-stage model aliases

`StudioSettings` today only has coarse overrides (`AsrModelOverride`, `TranslationModelOverride`, …). GenAi override resolves to `whisper-tiny-genai`, not `whisper-small-genai`.

**Add to `StudioSettings`:**

```csharp
IReadOnlyDictionary<string, string>? StageModelAliases = null  // keys: StageNames.* (asr, translation, tts, diarization, vad)
string? AppliedStarterPackId = null
string? AppliedStarterPackProfileId = null
```

**Wire consumers:**

- `RuntimeModelRequestFactory.CreateSelectionsFromSettings` — resolves aliases with precedence: explicit `InferenceModelPreferences` > `StageModelAliases` > override enums > defaults. Used by SDK dubbing pre-flight and default pipeline readiness.
- `AvaloniaMainWindowViewModel.PipelineUi` `CreateRuntimeSelections` — seed `GetSelectedModelAlias` from `StageModelAliases` when pipeline row has no selection (Avalonia follow-up).
- `PipelineStageModelCatalog.ResolveInitialSelection` — prefer `StageModelAliases[stageKey]` before first inventory option (Avalonia follow-up).

### 2. CpuSafe → fast

`HardwarePresetRecommendation.ToModelTierPreference`: change `CpuSafe => "fast"`.

### 3. Translation override for Premium

Premium apply sets `TranslationModelOverride.Madlad` (maps to `madlad400`). Basic/Balanced set `TranslationModelOverride.Auto` **and** `StageModelAliases["translation"] = "phi-4-mini"` so router does not depend on download order.

---

## Pack apply contract

`StarterPackApplyService.ApplyAsync(pack, profileId, hardwareProfile)` mutates `StudioSettings` as follows:

| Field | Basic | Balanced (`default`) | Balanced (`balanced-multilingual`) | Premium (`default`) |
|---|---|---|---|---|
| `ModelTierPreference` | `fast` | `balanced` | `balanced` | `quality` |
| `AppliedStarterPackId` | `basic` | `balanced` | `balanced` | `premium` |
| `AppliedStarterPackProfileId` | `default` | `default` | `balanced-multilingual` | `default` |
| `AsrModelOverride` | `OnnxRuntime` | `GenAi` | `Auto` | `GenAi` |
| `TranslationModelOverride` | `Auto` | `Auto` | `Auto` | `Madlad` |
| `TtsModelOverride` | `Kokoro` | `Kokoro` | `Kokoro` | `Chatterbox` |
| `StageModelAliases["asr"]` | `whisper-tiny` | `whisper-small-genai` | `qwen3-asr-0.6b` | `whisper-medium-genai` |
| `StageModelAliases["translation"]` | `phi-4-mini` | `phi-4-mini` | `phi-4-mini` | `madlad400` |
| `StageModelAliases["tts"]` | `kokoro-onnx` | `kokoro-onnx` | `kokoro-onnx` | `chatterbox-turbo-onnx` |
| `StageModelAliases["diarization"]` | (omit) | `sortformer-4spk` | `sortformer-4spk` | `sortformer-4spk` |

**Variant overrides** (`ModelVariantOverrideKeys.Build(stage, alias)`):

| Stage | Alias | `cpu_safe` | `balanced_gpu` / `turbo_gpu` |
|---|---|---|---|
| `vad` | `silero-vad` | `int8` | `fp16` |
| `asr` | per profile | see runtime table below | see runtime table below |
| `translation` | `phi-4-mini` | `cpu-int4` | `gpu-int4` |
| `translation` | `madlad400` | `quantized` | `default` |
| `tts` | `kokoro-onnx` | `default` | `default` |
| `tts` | `chatterbox-turbo-onnx` | `q4` | `fp16` |

**Hardware overrides** (`HardwareOverrides` keys from `HardwareOverrideCatalog`):

| Key | `cpu_safe` | `balanced_gpu` | `turbo_gpu` |
|---|---|---|---|
| `Vad` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `AsrGenAi` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `AsrOnnxRuntime` | `Cpu` | `DirectMl` | `DirectMl` |
| `Translation` | `Cpu` | `DirectMl` / `Cuda` | `TensorRTRtx` / `Cuda` |
| `Tts` | `Cpu` | `DirectMl` | `TensorRTRtx` / `DirectMl` |
| `Diarization` | `Cpu` | `DirectMl` / `Cuda` | `Cuda` |

Use `null` provider (= Auto) when pack JSON says `"execution_provider": "auto"` for a stage; omit key instead of writing Auto explicitly.

---

## Services and file layout

| Artifact | Project | Path |
|---|---|---|
| Pack JSON (bundled) | `Trackdub.Composition` | `StarterPacks/basic.json`, `balanced.json`, `premium.json` |
| Copy to output | `Trackdub.Composition.csproj` | `<Content Include="StarterPacks\*.json" CopyToOutputDirectory="PreserveNewest" />` |
| `StarterPackDefinition` record | `Trackdub.Contracts` | `StarterPacks/StarterPackDefinition.cs` |
| `IStarterPackCatalog` | `Trackdub.Contracts` | `StarterPacks/IStarterPackCatalog.cs` |
| `StarterPackCatalog` | `Trackdub.Composition` | `StarterPacks/StarterPackCatalog.cs` |
| `StarterPackValidator` | `Trackdub.Composition` | `StarterPacks/StarterPackValidator.cs` |
| `StarterPackDownloadService` | `Trackdub.Composition` | `StarterPacks/StarterPackDownloadService.cs` |
| `StarterPackApplyService` | `Trackdub.Composition` | `StarterPacks/StarterPackApplyService.cs` |
| CLI commands | `Trackdub.Cli` | extend `Commands/ModelsCommand.cs` |
| CLI handlers | `Trackdub.Cli` | `Handlers/StarterPacksHandler.cs` |
| TUI | `Trackdub.Cli` | extend `Tui/Screens/ModelsTuiScreen.cs` |
| DI registration | `Trackdub.Composition` | `CompositionRoot.cs` |

### `IStarterPackCatalog`

```csharp
Task<IReadOnlyList<StarterPackSummary>> ListAsync(CancellationToken ct);
Task<StarterPackDefinition> GetAsync(string packId, CancellationToken ct);
string UserPacksDirectory { get; }  // Path.Combine(AppContext.BaseDirectory, "StarterPacks")
```

Load order: parse bundled `StarterPacks/*.json` from `AppContext.BaseDirectory`, then overlay `{UserPacksDirectory}/*.json` (user wins on duplicate `id`).

### `StarterPackDownloadService`

- Input: pack id, profile id, optional progress `IProgress<ModelDownloadProgress>`.
- Resolve required `model_id` list = all `models[].required == true` plus profile's `asr_model_id` if not already listed.
- For each id: `IModelDownloadOrchestrator.DownloadAsync` (same as `models download`).
- Do not call Olive or `IStudioSettingsService.SaveAsync`.
- Return `StarterPackDownloadResult` with per-model success/failure.

### `StarterPackApplyService`

- Input: pack id, profile id, hardware profile (or auto-detect).
- Validate all required models are `Ready` or `Installed` in `IModelInventoryService` (warn, do not block apply — user may apply before download completes; document that pipeline will skip until ready).
- Build `StudioSettings` patch per **Pack apply contract** above; `IStudioSettingsService.SaveAsync`.
- Return `StarterPackApplyResult` listing what changed.

---

## CLI command surface

Register under `trackdub models packs` in `ModelsCommand.Create()`.

```text
trackdub models packs list [--json]
trackdub models packs show <pack-id> [--profile <id>] [--json]
trackdub models packs download <pack-id> [--profile <id>] [--yes]
trackdub models packs apply <pack-id> [--profile <id>] [--hardware-profile cpu_safe|balanced_gpu|turbo_gpu] [--accept-voice-cloning-consent] [--yes]
trackdub models packs add <model-id> [--yes]          # optional model; no settings change
trackdub models packs add-pair <source>-<target> [--yes]  # resolves opus manifest entry; optional
```

**`list --json` shape:**

```json
{
  "packs": [
    {
      "id": "balanced",
      "display_name": "Balanced / Mid",
      "tier_preference": "balanced",
      "profiles": ["default", "balanced-multilingual"],
      "required_model_ids": ["onnx-community/silero-vad", "..."],
      "optional_model_ids": ["..."],
      "applied": false
    }
  ],
  "recommended_pack_id": "balanced",
  "hardware_profile": "balanced_gpu"
}
```

`recommended_pack_id` from `IHardwareProfilerService` + simple rules (no GPU → `basic`, etc.).

Exit codes: reuse `Program.ExitSuccess`, `ExitPipelineFailure`, `ExitCancelled` from existing CLI.

---

## TUI implementation (`ModelsTuiScreen`)

Extend `src/Trackdub.Cli/Tui/Screens/ModelsTuiScreen.cs`. Keep existing flat inventory table; add pack mode via overlay picker (same pattern as download menu).

### Footer (no overlay)

```text
Models actions:  p  packs menu   d  download menu   a  all missing   v  verify
```

### Packs menu (`p`)

Picker choices:

1. **List packs** — table: Pack | Tier | Profile | Required | Ready | Applied
2. **Download pack…** — pick pack → pick profile (if >1) → confirm → sequential download with existing progress reporting
3. **Apply pack…** — pick pack → profile → confirm hardware profile (default from profiler) → apply → status message
4. **Add optional model…** — pick from current pack's `optional_models` or full missing list
5. Back

### Display conventions

- Use `TuiMarkup.FormatModelLabel` for model ids.
- GPU copy: "GPU-optimized (DirectML)" on Windows; "CUDA" on Linux only when `OperatingSystem.IsLinux()`.
- After apply, set `context.StatusMessage` with pack id + profile + hardware profile.
- Premium apply: if pack includes `chatterbox-turbo-onnx` and consent not yet recorded, show consent picker before apply (mirror Avalonia consent flags in `StudioSettings` if they exist, or set a new `VoiceCloningConsentAccepted` bool).

### v1 TUI truth model (implemented 2026-06-12)

Starter packs are an **onboarding layer** over the existing manifest/runtime planner. CLI and Avalonia call **`IStarterPackCoordinator`** (Composition) only; they do not parse pack JSON or compute planner truth. `StarterPacksHandler` is a thin Cli adapter over the coordinator.

### Add a bundled pack once

1. Add JSON under `src/Trackdub.Composition/StarterPacks/` (and csproj copy rule if needed).
2. Extend `StarterPackApplyContract` when new override mapping is required.
3. Add/adjust validator tests in `Trackdub.Composition.Tests`.
4. **Stop.** CLI TUI and Avalonia pick up the pack via `ListAsync()` — do not duplicate pack logic in shell projects.

**Apply coverage (architecture tests):** bundled shipping packs must have an apply contract entry or explicit `pack_kind: cloud` branch (cloud in a later PR). User packs (future) validate through data-driven `apply` JSON, not `StarterPackApplyContract`.

**Pack panel columns:** Pack | Profile | Required | Installed | Status

| Term | Meaning |
|---|---|
| **Installed** | Required model files present with cache state `Ready` or `Installed` and checksum-valid inventory |
| **Runtime-ready** | Planner + EP smoke say the selected variant/EP is runnable (display-only in v1.1; never gates Apply in v1) |

**Status values (pack-level):**

- `applied` — `AppliedStarterPackId` matches
- `recommended` — hardware profiler suggestion (`CpuSafe` → `fast` → Basic pack)
- `license review needed` — any **required** model has `commercial_use_verified: false` in manifest

**Apply gates (all must pass; transactional, all-or-nothing):**

1. `Installed == Required` for the pack/profile (checksum-valid; not smoke-verified).
2. Every **required** model has `commercial_use_verified: true`. Block with `License review needed: {alias} is not commercial-use verified.`
3. Consent metadata satisfied for models with `requires_user_consent` / voice-cloning flags.

**Download vs Apply:** Download never mutates `StudioSettings`. Apply persists aliases, variants, EP keys, tier, and applied-pack IDs in one write.

**v1 apply-ready reality:** Until manifest verification catches up (today: `sortformer-4spk` on Balanced/Premium; `phi-4-mini` may also block Basic), only packs with all required models `commercial_use_verified: true` can Apply. Balanced/Premium remain downloadable but Apply-blocked.

**Footer:**

```text
p packs   d ad-hoc download   a all missing   v verify
```

**`p` menu (two separate actions, no combined shortcut):**

1. Download pack… — files only; works when license review is pending.
2. Apply pack… — blocked until installed + commercial-verified + consent OK.

**Key schemes (do not conflate):**

| Dictionary | Key source | Examples |
|---|---|---|
| `StageModelAliases` | `StageNames` | `asr`, `translation`, `tts`, `diarization` |
| `HardwareOverrides` | `HardwareOverrideCatalog` | `AsrGenAi`, `AsrOnnxRuntime`, `Separation`, `Diarization` |

Apply **merges** pack-owned keys into existing `HardwareOverrides` and `ModelVariantOverrides`; unrelated user overrides are preserved.

**Override precedence:**

```text
Explicit session/project override
  > StageModelAliases from applied pack
  > existing model override enum (Asr/Translation/Tts)
  > built-in default / planner ranking
```

Implemented in `RuntimeModelRequestFactory.CreateSelectionsFromSettings`; SDK dubbing/readiness paths load `StudioSettings` and call this helper.

### Tier-band view (v1 stretch inside TUI)

Optional sub-mode `t` toggles grouping:

- **Pack: Basic** — manifest models whose `tier` is `fast` and appear in `basic.json`
- **Pack: Balanced** — `balanced` tier + balanced pack list
- **Pack: Premium** — `quality`/`accurate` + premium pack list
- **Optional add-ons** — union of all `optional_models` across packs + opus-mt entries
- **All** — current flat list (default)

Implementation: filter `ModelInventoryEntry` by manifest `tier` and pack membership; no new inventory API required.

---

## Avalonia implementation (`ModelManagerViewModel`)

v1 scope (no first-run wizard gate — that is v1.1):

1. **Tier band filter** dropdown: All | Basic pack | Balanced pack | Premium pack | Optional add-ons.
2. **Applied pack badge** in Model Manager header when `AppliedStarterPackId` is set (read `IStudioSettingsService`).
3. **Actions** (mirror CLI): Download pack, Apply pack — call shared `StarterPackDownloadService` / `StarterPackApplyService` from Composition (inject into `ModelManagerViewModel`).
4. **Consent** — reuse existing chatterbox consent UI before Premium apply.

First-run chooser (v1.1): when `ShowLocalModelsAtStartup` and no models ready and no `AppliedStarterPackId`, show pack recommendation dialog.

---

## Fallback and degradation

If planner/smoke rejects an EP:

- Substitute next EP in pack order for that hardware profile (TRT-RTX → DirectML → CPU on Windows).
- If model tier fails VRAM gate, log degradation; do not auto-download a different pack.
- User-visible message template:

```text
GPU path unavailable for {alias} ({model_id}). Using {variant} on {execution_provider}.
```

Structured downgrade ladder (separate task): persist fallback reason in `PipelineDegradationRecord` when apply-time EP substitution occurs.

---

## Tests

| Layer | Project | Cases |
|---|---|---|
| Validator | `Trackdub.Composition.Tests` or `Trackdub.Application.Tests` | Rejects unknown model_id, experimental lane, bad variant, invented olive path |
| Catalog | same | Bundled load, user override wins, missing file graceful |
| Apply | `Trackdub.Application.Tests` | Basic apply sets `StageModelAliases`, tier, variant keys; Premium sets Madlad override + chatterbox |
| CpuSafe | `Trackdub.Domain.Tests` | `ToModelTierPreference(CpuSafe) == "fast"` |
| CLI | `Trackdub.Cli.Tests` or `Trackdub.Sdk.Tests` | `models packs list --json` smoke; download/apply integration with fakes |
| Readiness | `Trackdub.Sdk.Tests` | After apply+bundle download fakes, `bundle-needed` reflects spine |

---

## Implementation order

| Step | Owner | Deliverable |
|---|---|---|
| 1 | Composition | Pack JSON files + `StarterPackDefinition` + validator |
| 2 | Contracts + Composition | `IStarterPackCatalog`, loader, DI |
| 3 | Contracts + Infrastructure | `StageModelAliases`, `AppliedStarterPack*` on `StudioSettings` + JSON persistence |
| 4 | Application | Wire `StageModelAliases` in `RuntimeModelRequestFactory` + Avalonia pipeline seed |
| 5 | Domain | CpuSafe → `fast` |
| 6 | Composition | Download + Apply services |
| 7 | Cli | `models packs *` commands + `StarterPacksHandler` |
| 8 | Cli TUI | Packs overlay on `ModelsTuiScreen` |
| 9 | App.Avalonia | Model Manager tier filter + apply/download buttons |
| 10 | Later | First-run wizard, structured downgrade ladder, `models packs optimize` pack scope |

---

## Acceptance checks

- [ ] Basic required downloads: `silero-vad`, `whisper-tiny`, `Kokoro`, `Phi-4-mini` (by model_id).
- [ ] Balanced required: above spine + `sortformer` + `whisper-small` (default profile) + `Phi-4-mini`.
- [ ] Premium required: `sortformer`, `whisper-medium`, `chatterbox-turbo`, `madlad400`, `silero-vad`.
- [ ] No Opus pair in any pack `models[].required`.
- [ ] No co-download of both `whisper-small` and `qwen3-asr-0.6b` for same profile apply.
- [ ] `download` never runs Olive or changes `settings.json`.
- [ ] `apply` persists `ModelTierPreference`, `StageModelAliases`, overrides, variant keys, hardware keys, `AppliedStarterPackId`.
- [ ] CLI `models packs list --json` and TUI packs menu work offline against bundled JSON only.
- [ ] `CpuSafe` → `fast`.
- [ ] Premium apply blocked without voice cloning consent.
- [ ] Windows UI strings say GPU-optimized, not CUDA.

---

## References

| Topic | Path |
|---|---|
| Manifest | `src/Trackdub.Inference/Runtime/ModelManifest/bundled-models.manifest.json` |
| Tier bias | `src/Trackdub.Inference/Runtime/Planning/RuntimePlannerRankingStrategy.cs` |
| Hardware profiler | `src/Trackdub.Domain/HardwareProfiler.cs` |
| Hardware overrides | `src/Trackdub.Application/Runtime/HardwareOverrideCatalog.cs` |
| Variant keys | `src/Trackdub.Contracts/IStudioSettingsService.cs` (`ModelVariantOverrideKeys`) |
| Runtime selections | `src/Trackdub.Application/Transcripts/RuntimeModelRequestFactory.cs` |
| Translation router | `src/Trackdub.Inference.Onnx/Translation/TranslationLanguageRouter.cs` |
| Model download | `src/Trackdub.Contracts/IModelDownloadOrchestrator.cs` |
| Models CLI | `src/Trackdub.Cli/Commands/ModelsCommand.cs`, `Handlers/ModelsHandler.cs` |
| Models TUI | `src/Trackdub.Cli/Tui/Screens/ModelsTuiScreen.cs` |
| Storage paths | `src/Trackdub.Infrastructure/Settings/TrackdubStoragePaths.cs` |
| Pipeline readiness | `src/Trackdub.Sdk/TrackdubPipelineReadinessChecker.cs` |
| Windows EP ADR | `docs/adr/ADR-0002-windows-ml-provider-strategy.md` |
| CLI/TUI design | `docs/superpowers/specs/2026-06-06-trackdub-cli-tui-design.md` |
