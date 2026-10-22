# ADR-0002: Windows ML-first execution provider strategy

- Status: Accepted
- Date: 2026-05-23
- Supersedes in part: [ADR-0001](ADR-0001-winui3-windows-ml.md) (DirectML-as-baseline wording only)

## Context

Microsoft positions **Windows ML** as the forward path for Windows ONNX Runtime deployments. The ONNX Runtime shipped with Windows ML supports explicit execution provider (EP) selection and optional device policies (`MAX_PERFORMANCE`, `PREFER_NPU`, `MAX_EFFICIENCY`, and others). Microsoft recommends starting with **explicit EP selection** for predictability, then experimenting with device policies.

**DirectML** remains supported under sustained engineering, but new feature work has moved to Windows ML. Windows ML still includes CPU and DirectML legacy providers, and adds dynamic provider acquisition and registration for certified vendor EPs (for example AMD MIGraphX, Intel OpenVINO, Qualcomm QNN, and AMD VitisAI). NVIDIA TensorRT RTX is handled separately as the standalone ONNX Runtime EP ABI plugin.

Trackdub already implements much of this layering in `Trackdub.Inference.Onnx` (Windows ML bootstrap, catalog EP registration, TRT RTX plugin registration, smoke tests, fallback chains). Product language and some planning documents still describe DirectML as the centerpiece Windows GPU strategy. That mismatch confuses agents, contributors, and users.

## Decision

On Windows, Trackdub treats **Windows ML as the ONNX integration surface**, not DirectML alone.

1. **Windows ML layer** — Bootstrap, certified catalog EP registration, and `OrtEnv.GetEpDevices()` device enumeration live in `Trackdub.Inference.Onnx` (for example `WindowsMlExecutionProviderBootstrapper`, `WindowsMlProviderRegistrationPolicy`, `WindowsExecutionProviderBootstrapper`). UI and Application layers must not assemble EP policy.

2. **TensorRT RTX plugin route** — `ExecutionProviderKind.TensorRTRtx` is the standalone ORT EP ABI plugin route on Windows. Trackdub locates a bundle containing `onnxruntime_providers_nv_tensorrt_rtx.dll`, `tensorrt_rtx_1_7.dll`, and `tensorrt_onnxparser_rtx_1_7.dll`; registers the plugin with `OrtEnv.RegisterExecutionProviderLibrary`; enumerates `OrtEnv.GetEpDevices()`; and appends the `NvTensorRTRTXExecutionProvider` GPU device explicitly. It does not call Windows ML `ExecutionProvider.TryRegister`, `EnsureAndRegisterCertifiedAsync`, or `SetEpSelectionPolicy` for TRT RTX.

3. **Preferred acceleration** — Where a model and stage allow it, prefer **TensorRT RTX plugin** or certified catalog execution providers (for example `Migraphx`) before generic GPU fallback. Global milestone probe order in `Milestone5PlanningPolicy.SupportedProvidersThisMilestone` reflects this: TensorRT RTX → MIGraphX → native TensorRT/CUDA (Linux or advanced Windows) → DirectML → CPU.

4. **DirectML as legacy GPU fallback** — `ExecutionProviderKind.DirectMl` uses the Windows ML **packaged DirectML route** (`WindowsMlProviderRegistrationRoute.PackagedDirectMl`). It is a supported compatibility fallback when catalog/plugin/vendor EPs are unavailable or fail smoke tests—not the primary forward acceleration strategy.

5. **Explicit selection and gates** — Runtime routing stays **manifest-driven, stage-scoped, and smoke-test verified** per model/provider pair. Provider registration does not imply model readiness. Per-model allowed-provider lists (for example Kokoro CPU-only) remain authoritative.

6. **User-visible honesty** — Discovery messages, Model Manager hints, and hardware override labels should describe TRT RTX as a plugin route, Windows ML catalog EPs as catalog routes, and DirectML as legacy GPU fallback where shown explicitly.

### Mapping to code

| Concept | Location |
|--------|----------|
| Provider enum | `ExecutionProviderKind` in `Trackdub.Domain/Common/RuntimePlanning.cs` |
| Milestone probe order | `Milestone5PlanningPolicy` in `StageRuntimeRequirements.cs` |
| Packaged DirectML vs catalog EP | `WindowsMlProviderRegistrationRoute` in `WindowsMlProviderRegistrationPolicy.cs` |
| TRT RTX plugin location/registration | `TensorRtRtxPluginLocator.cs`, `TensorRtRtxPluginService.cs` |
| Bootstrap and fallback | `WindowsExecutionProviderBootstrapper.DetermineFallbackProviderAsync` |
| Discovery | `OnnxExecutionProviderDiscovery` |
| Session creation | `OnnxExecutionSessionFactory` |

## Consequences

Positive:

- Aligns documentation and UI copy with Microsoft's Windows ML direction and with existing implementation seams.
- Preserves predictability: explicit EP selection and smoke tests before claiming GPU readiness.
- Keeps vendor EP investment (TensorRT RTX plugin, MIGraphX catalog) as first-class without pretending DirectML is the modern default.

Negative:

- Contributors must learn the layered model (TRT RTX plugin / Windows ML catalog EP → DirectML → CPU), not a single "GPU on" switch.
- Bundled manifest `expected_runtime` tokens use `trt-rtx|windows-ml|onnxruntime-migraphx|onnxruntime-directml` for standard ONNX models where appropriate.

## Non-goals (this ADR)

- Changing planner probe order (global order was already correct; Phase 2 aligned stage allow-lists).
- Device policies are optional via advanced studio setting (Phase 3, 2026-05-23); default remains explicit EP selection.
- Adding OpenVINO or QNN catalog routes before product prioritization.

## Device policies (Phase 3)

Optional advanced studio setting `WindowsMlExecutionDevicePolicy` (default `Explicit`). Non-default values call `SessionOptions.SetEpSelectionPolicy` for Windows ML catalog/device-policy routes (`DirectMl`, `Migraphx`) and skip explicit catalog-device append. TensorRT RTX plugin and native CUDA/TensorRT on Windows ignore device policy. Details: [windows-ml-phase-3-device-policies.md](../reference/windows-ml-phase-3-device-policies.md).

## Future work

Deferred items; not required for accepting this strategy ADR:

1. **Additional catalog EPs** — OpenVINO, QNN, and other Windows ML-registered vendor EPs when manifest and smoke coverage exist. Phase 5 documents gates and adds compile-time stubs only (see below).

## Phase 5 — Catalog EP expansion (OpenVINO, QNN)

**Status (5c):** Documentation, operator checklist, and **thin code stubs** only. No milestone probe-order change and no fake readiness.

### OpenVINO dual path

- **Standalone ExecutionProviderKind.OpenVino** — existing Linux/optional Windows install path via IOpenVinoAvailabilityProvider and Infrastructure component downloader. Registration ≠ readiness; smoke still required per model.
  - **Update (2026-10):** on Linux, `OpenVino` sessions now run on the OpenVINO plugin EP from `LinML.Catalog` (pinned, sha256-verified, installed and registered by the catalog), the Linux counterpart to `OpenVinoCatalog`. Readiness and discovery come from the catalog; the standalone runtime component no longer backs Linux sessions. See `docs/reference/gpu-execution-providers.md`.
- **Future ExecutionProviderKind.OpenVinoCatalog** — WinML catalog Intel EP on Windows, distinct enum value so planner, discovery, and session code do not conflate with standalone OpenVINO. Same honesty rules as MIGraphX: catalog registration and smoke before allow-list or probe-order changes.

### QNN / NPU path

- **Future ExecutionProviderKind.Qnn** — Qualcomm catalog EP registered through Windows ML when certified packages are present.
- **WindowsMlExecutionDevicePolicy.PreferNpu** is a *session selection hint* only. It must not be interpreted as “QNN ready” or “NPU model ready.” Discovery reports QNN as unavailable until Phase 5 gates pass.

### Gating checklist (before probe-order or allow-list changes)

1. Catalog provider id confirmed against ExecutionProviderCatalog / Microsoft docs for the target Windows ML SDK.
2. WindowsMlProviderRegistrationPolicy + OnnxExecutionSessionFactory append path implemented and covered by tests.
3. Per-stage smoke on representative hardware (matrix rows in [windows-ml-stage-provider-matrix.md](../reference/windows-ml-stage-provider-matrix.md)).
4. Bundled manifest expected_runtime tokens updated where product commits to a catalog route.
5. Model Manager / settings copy states registration vs download vs smoke vs stage success separately.
6. Planner smoke failure → fall through to next provider (no stuck “ready” state).

### Explicit non-goals for 5c

- No change to Milestone5PlanningPolicy.SupportedProvidersThisMilestone probe order until matrix smoke passes for the new EP on target hardware.
- Stubs must report **unavailable** in discovery and **not enabled** in registration/session paths — never “GPU ready” or “catalog registered therefore ready.”

Operational detail: [windows-ml-phase-5-catalog-eps.md](../reference/windows-ml-phase-5-catalog-eps.md).

## References

- [DirectML overview](https://learn.microsoft.com/windows/ai/directml/dml) (sustained engineering; legacy GPU path)
- [Windows ML overview](https://learn.microsoft.com/windows/ai/new-windows-ml/overview)
- [Windows ML supported execution providers](https://learn.microsoft.com/windows/ai/new-windows-ml/supported-execution-providers)
- [Windows ML provider selection](https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers)
- [Installing and registering Windows ML EPs](https://learn.microsoft.com/windows/ai/new-windows-ml/initialize-execution-providers)
- [ONNX Runtime TensorRT RTX EP](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)
- [ONNX Runtime plugin EP library usage](https://onnxruntime.ai/docs/execution-providers/plugin-ep-libraries/usage.html)
- Seams: [`docs/reference/migraphx-phase0-seams.md`](../reference/migraphx-phase0-seams.md)

## Phase 4 closeout (2026-05-23)

ORT native load order prefers the managed ONNX Runtime package before app-base DLLs; session pool eviction and policy-cache invalidation run when hardware settings change. Operational checklist: [windows-ml-phase-4-closeout.md](../reference/windows-ml-phase-4-closeout.md).

## Phase 6: portable-build TRT RTX and GenAI exclusions (2026-09)

Two corrections to the original layering assumptions:

- **TRT RTX is not Windows-ML-bound.** The EP ABI plugin route uses plain ONNX
  Runtime extensibility (`RegisterExecutionProviderLibrary` + `GetEpDevices`),
  so the portable `net10.0` build now bootstraps TRT RTX on Windows and Linux.
  `OnnxRuntimeBuildCapabilities` records per-TFM reach: portable `net10.0`
  supports CPU + TRT RTX; DirectML and WinML catalog EPs still require
  `net10.0-windows10.0.19041.0`. Discovery reports unrunnable providers as
  unavailable (with the required TFM named), and the CLI warns when a requested
  provider cannot run in the current build.
- **ORT GenAI model loads never see TensorRT.** GenAI's `NvTensorRtRtx` device
  terminated the host process (native stack overflow) on a bundled
  `qwen-instruct` model; a fatal crash cannot be gated by a smoke test, so
  `whisper-genai`, `phi-genai`, and `qwen-instruct` have engine-family provider
  overrides excluding TensorRT, and the smoke tester refuses the combination
  outright. This mirrors the existing `opus-mt`/`madlad` InferenceSession ctor
  stack-overflow exclusion.

See [gpu-execution-providers.md](../reference/gpu-execution-providers.md) for the
operator-facing build/provider matrix.

## Phase 5 licensing policy (2026-05-23)

Vendor EPs and plugin bundles carry third-party licenses that require explicit user acceptance before installation/registration. The following policy is now enforced:

- **License families:** Four families gate the certified EP/plugin families — `AmdRyzenAi` (MIGraphX + VitisAI), `NvidiaTensorRtRtx`, `IntelOpenVino`, `QualcommQnn`.
- **One-time per-machine acceptance** is persisted in `StudioSettings` (flags `AmdRyzenAiLicenseAccepted`, `NvidiaTensorRtRtxLicenseAccepted`, `IntelOpenVinoLicenseAccepted`, `QualcommQnnLicenseAccepted`).
- **`ILicenseConsentService`** (`Trackdub.Application.Runtime`) is the app-layer contract. The Avalonia shell provides `AvaloniaLicenseConsentService`, which shows `EpVendorLicenseDialog` on the UI thread and persists acceptance on confirmation.
- **Per-EP install commands** in `ModelManagerViewModel` call `EnsureAcceptedAsync` before beginning installation. If the user declines, the install aborts and a status message is shown.
- **Bulk install** (`InstallAllCertifiedCatalogAsync`) iterates all four families and requires each to be accepted before proceeding.
- **"View license" commands** (`ViewMigraphxLicenseCommand`, `ViewTensorRtRtxLicenseCommand`, `ViewOpenVinoLicenseCommand`, `ViewQnnLicenseCommand`, `ViewVitisAiLicenseCommand`) open the dialog in informational mode without modifying stored flags.
- License metadata and external links are provided by `LicenseMetadataProvider` (in `EpVendorLicenseDialog.axaml.cs`). Third-party notices are documented in `THIRD_PARTY_NOTICES.md`.
