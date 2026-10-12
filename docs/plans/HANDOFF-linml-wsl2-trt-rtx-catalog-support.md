# Handoff: native WSL2 TensorRT-RTX support in `LinML.Catalog`

**For:** an agent working in `trackdubllc/LinML` (branch from `main`, current release v0.4.0).
**Why now:** Trackdub (PRs trackdubllc/Trackdub#470 → #471 → #472) consumes `LinML.Catalog` 0.4.0 and had to
work around the catalog not knowing about the WSL2 TensorRT-RTX build. This change moves that workaround
into LinML, where the pins, hashes and install layout already live.

## 1. Goal

On WSL2 with NVIDIA GPU passthrough, `ExecutionProviderCatalog.Find(ProviderId.TensorrtRtx).EnsureReadyAsync(new EnsureOptions(AllowDownloads: true))`
must end with the provider **Ready and exposing the GPU**, with no manual script step, no `LD_LIBRARY_PATH`, no
`LINML_EP_LIBRARIES`, and no process restart between install and use.

Today it ends `NoDevices` on WSL2 (see section 2), and consumers must pre-register LinML's patched build themselves.

## 2. Current behavior (v0.4.0), with file references

- `src/LinML.Catalog/PluginPackageSpec.cs` → `KnownPlugins.All = [OpenVino, TensorrtRtx, MigraphX]`. `TensorrtRtx` is the stock
  PyPI wheel `onnxruntime_ep_nv_tensorrt_rtx_cu13-0.4.0` (sha256 `68b8f7306b50cf76daf997b8be24ec3813498c15d48e0ddd211db14f2a9daa19`),
  installed to `ep-plugins/trt-rtx/onnxruntime_ep_nv_tensorrt_rtx`, registration name `NvTensorRTRTXExecutionProvider`, min ORT 1.24.
- `src/LinML.Catalog/PluginExecutionProvider.cs` → `EnsureReadyAsync`: returns Ready if the EP name already has devices; otherwise
  installs the wheel (`PluginInstaller.InstallAsync`), calls `TryRegister`, then reports `NoDevices` if registration yields zero devices.
  On WSL2 it reports `NoDevices` with the text "Use custom build or bare-metal Linux." (`IsWsl()` is `internal` there).
- `src/LinML.Catalog/PluginLibraryRegistry.cs` (public, static): ORT registration is process-wide and **one registration per EP name**.
  Registering the same name with a different path throws `ArgumentException`. A name registered by the stock plugin cannot be swapped for the
  patched one in the same process.
- `src/LinML.Cli/ProviderArgs.cs`: the CLI already honors `TRT_RTX_CUSTOM_EP` and `LINML_EP_LIBRARIES`, and preloads bundled libraries from the
  chosen plugin's own directory (`PluginInstaller.LoadBundledLibraries`, `internal`). The catalog does not read `TRT_RTX_CUSTOM_EP`.
- `scripts/fetch-ep-plugins.sh trt-rtx-wsl2` and `scripts/env-gpu-deps.sh` already implement the opt-in patched build (precedence:
  `TRT_RTX_CUSTOM_EP` → vendored WSL2 build → stock wheel). The C# catalog has no equivalent.
- `tests/LinML.Catalog.Tests/ScriptPinAgreementTests.cs` parses `fetch-ep-plugins.sh` and asserts every `KnownPlugins` entry matches a script pin
  (URL, sha256, install subdirectory). Any new pin must keep this test green and have a script counterpart.

## 3. The WSL2 build that already exists

Release `ep-trt-rtx-wsl2-0.4.0-wsl2p1` (prerelease):

| Item | Value |
|---|---|
| Asset | `linml-trt-rtx-ep-0.4.0-wsl2p1-linux-x64.zip` (~1.8 MB) |
| URL | `https://github.com/trackdubllc/LinML/releases/download/ep-trt-rtx-wsl2-0.4.0-wsl2p1/linml-trt-rtx-ep-0.4.0-wsl2p1-linux-x64.zip` |
| sha256 | `e87adedba3dfc86ea96cd7c6f5b9a965de584aeda971ee7792ae6d1e0074d2df` |
| Zip prefix / install dir | `linml_trt_rtx_ep/` → `ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/` |
| Library | `libonnxruntime_providers_nv_tensorrt_rtx.so` (RUNPATH `$ORIGIN`) |
| Also in the zip | `build-info.json`, `PATCH-NOTICE.md`, `licenses/` (Apache-2.0 §4(b) modification notice) |
| Source | `tonythethompson/TensorRT-RTX-EP-ABI` @ `6777dafa…` (upstream PR #12 head); upstream issue #13 |

It ships **only the EP `.so`**. Verified on a real WSL2 + RTX 5070 host (`readelf -d`): it `NEEDED`s
`libcudart.so.13`, `libtensorrt_rtx.so.1`, `libtensorrt_onnxparser_rtx.so.1` (all provided by the stock wheel's directory), plus the system
`libcuda.so.1` and `libnvidia-ml.so.1` (projected from `/usr/lib/wsl/lib`, on the loader path by default in WSL via `ld.wsl.conf`).

## 4. What Trackdub does today (the workaround to replace)

`LinMlWsl2TensorRtRtxPlugin` in Trackdub PR #471 (`src/Trackdub.Inference.Onnx/ExecutionProviders/LinMl/LinMlWsl2TensorRtRtxPlugin.cs`):
when running under WSL and the patched `.so` is already vendored, it `NativeLibrary.Load`s every `*.so*` in the stock plugin directory
(except the plugin itself), then calls `PluginLibraryRegistry.Register("NvTensorRTRTXExecutionProvider", <patched path>)` **before** the catalog
runs, so the catalog sees devices and reports Ready. Known gaps of that workaround, which the LinML change should remove:

1. It never downloads the patched build (it can't verify/pin it).
2. First install on WSL2 registers the stock plugin (zero devices), after which the patched build cannot replace it until a restart.
3. Every consumer has to re-implement the library preload and the WSL2 detection.

Verified end to end on WSL2/RTX 5070 with that workaround: registration succeeds, ORT lists a `NvTensorRTRTXExecutionProvider / GPU` device, and
LinML's own fixture (`tests/fixtures/model.onnx`, input `x`=[1..6] shape [3,2]) returns `[11, 22, 13, 24, 15, 26]`.

## 5. Proposed design

Make the WSL2 build a **variant of the existing `TensorrtRtx` provider**, not a new provider. The EP name is identical and ORT allows one
registration per name, so a second `ProviderId` would be misleading and `Find(ProviderId.TensorrtRtx)` would stay broken.

**Selection rule** (checked in `PluginExecutionProvider`, `Id == TensorrtRtx` only), highest precedence first:

1. `TRT_RTX_CUSTOM_EP` set to an existing file → use it (matches the CLI today; the catalog should honor it too).
2. WSL2 (`IsWsl()` **and** `/dev/dxg` exists) and not opted out → use the WSL2 build.
3. Otherwise → the stock wheel, exactly as today.

Opt-out: `LINML_TRT_RTX_WSL2=0` (name is a suggestion). Decide with the owner whether the default should be on (recommended: upstream's stock plugin
provably exposes zero devices on WSL2, so "stock" is never a useful choice there) or opt-in.

**Flow in `EnsureReadyAsync` when the WSL2 variant is selected** (this is what removes the restart problem — stock is vendored but never registered):

1. Existing-devices short-circuit stays first.
2. If stock files are missing → vendor stock (`PluginInstaller.InstallAsync`) if `AllowDownloads`, else `DownloadDisallowed`.
3. If the WSL2 library is missing → vendor it (new spec, sha256-verified) if `AllowDownloads`, else `DownloadDisallowed`.
4. Preload every `*.so*` in the **stock** directory by absolute path (generalize `LoadBundledLibraries` so the dependency directory can differ from the
   plugin's directory), keep the handles alive for the process lifetime.
5. `PluginLibraryRegistry.Register(RegistrationName, <WSL2 library path>)`. Never register the stock library on this path.
6. Zero devices → `NoDevices` with a WSL2-specific message (check `nvidia-smi`/`/dev/dxg`). **Do not fall back to stock**: the name is already taken.
7. Result `Detail` should say which build was registered; `ProviderEnsureStatus.Downloaded` when step 2 or 3 downloaded.

**Spec shape.** Add an internal overlay description alongside `PluginPackageSpec` (for example `PluginOverlaySpec(Base: TensorrtRtx, Url, Sha256,
PackagePrefix: "linml_trt_rtx_ep/", InstallSubdirectory: "ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep", LibraryFileName, MinimumOrtVersion 1.24)`) so
`KnownPlugins.All` and the existing three-plugin loops/tests are untouched. It reuses the installer's download + sha256 + staged `Promote` path.

**Installer change needed.** `PluginInstaller.ExtractFlat` only copies `*.so`, `*.so.*` and `*.json` and skips nested directories. The WSL2 zip also
carries `PATCH-NOTICE.md` and `licenses/`, which Apache-2.0 §4(b) requires to travel with the modified binary. Extend the extraction for overlays
(or all specs) to keep those files, and add a test that they land beside the library.

**Layout compatibility (important).** Keep the install path exactly `ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep/libonnxruntime_providers_nv_tensorrt_rtx.so`.
`fetch-ep-plugins.sh trt-rtx-wsl2` and `env-gpu-deps.sh` already use it, and existing developer machines (including the maintainer's) have it vendored,
so the catalog reuses those installs instead of downloading again.

**Do not break consumers.** Trackdub and its tests implement `IExecutionProvider` with fakes and construct `EnsureOptions(bool)`. Do **not** add
members to `IExecutionProvider` or change the `EnsureOptions` constructor. If the active build must be observable, add an optional member through
a default interface implementation (for example `string? ActiveBuild => null`), or put it in `ProviderEnsureResult.Detail` only.

## 6. Scope of changes (files)

- `src/LinML.Catalog/PluginPackageSpec.cs`: overlay spec + the pinned values from section 3.
- `src/LinML.Catalog/PluginExecutionProvider.cs`: selection rule, the flow above, honor `TRT_RTX_CUSTOM_EP`, replace the "Use custom build" `NoDevices` text for the case where the overlay is disabled or unavailable (point at `LINML_TRT_RTX_WSL2` / the script).
- `src/LinML.Catalog/PluginInstaller.cs`: dependency-directory preload; keep licence/notice files for overlays; reuse `Promote`/staging.
- `src/LinML.Cli/Commands/SetupCommand.cs` and `ProvidersCommand.cs`: on WSL2 `linml setup` installs the overlay; `providers list` shows which TensorRT-RTX build is active.
- `scripts/fetch-ep-plugins.sh`, `scripts/env-gpu-deps.sh`: keep behavior; make the pin agreement explicit (see tests).
- `docs/compatibility.md`, `README.md`, `src/LinML.Catalog/README.md`, `docs/adr/0002-multi-ep-and-plugins.md` (or a new ADR): replace "opt-in patched build" with the new default and the opt-out; keep the upstream issue/PR links and the "superseded when PR #12 merges" note.
- `CHANGELOG.md`: new minor version entry (suggest 0.5.0: behavior change for WSL2 hosts, no API break).

## 7. Tests

Use the existing seams (`FakeRegistrar`, `FakeWheelHandler`, `TempDir`, `PluginExecutionProvider(spec, root, installer, registrar, isSupportedPlatform)`);
add an injectable `isWsl2` (and environment-variable reader) so nothing depends on the host.

- WSL2 selected, nothing vendored, downloads allowed → downloads stock then overlay, **never registers stock**, registers the overlay path, `Downloaded`.
- WSL2 selected, both vendored → no HTTP requests, preload called with the **stock** directory, one `Register(name, overlayPath)`, `Ready`.
- WSL2 selected, downloads disallowed, overlay missing → `DownloadDisallowed`, no HTTP, no registration.
- WSL2 selected, registers but zero devices → `NoDevices`, WSL2 message, no stock fallback.
- Not WSL2 → behavior identical to today (regression guard for all three plugins).
- `LINML_TRT_RTX_WSL2=0` → stock path. `TRT_RTX_CUSTOM_EP` → used, beats the overlay.
- A name already registered with another path → surfaced as a clear failure, not an unhandled `ArgumentException`.
- sha256 mismatch on the overlay → `InvalidDataException` handled like the wheel (reported as `Failed`).
- Extraction keeps `PATCH-NOTICE.md`/`licenses/`.
- `ScriptPinAgreementTests`: extend so the overlay pin equals the `fetch_trt_rtx_wsl2` pin in the script (the parser currently keys on `fetch_*` functions with `WHEEL_URL`, so this function is already parsed; add the catalog-side assertion).
- Opt-in end-to-end test (skipped unless a WSL2 GPU is present), modelled on `OpenVinoCatalogEndToEndTests`.

## 8. Acceptance criteria

1. `dotnet test` green for `LinML.Catalog.Tests`, `LinML.Runtime.Onnx.Tests`, `LinML.Cli.Tests`; `ScriptPinAgreementTests` green.
2. On a WSL2 host with an NVIDIA GPU and **nothing vendored**: a small program calling `ExecutionProviderCatalog.GetDefault().Find(ProviderId.TensorrtRtx)!.EnsureReadyAsync(new EnsureOptions(true))`
   returns `Downloaded`, `Devices.Count >= 1`, and `AppendFromCatalog(..., [OrtHardwareDeviceType.GPU])` + running `tests/fixtures/model.onnx` yields `[11, 22, 13, 24, 15, 26]`. No env vars, no restart.
3. The same on a host that already has the `trt-rtx-wsl2` directory vendored by the script: no download, `Ready`.
4. On bare-metal Linux (or any non-WSL host): behavior is unchanged from 0.4.0 for all providers.
5. `linml providers list` / `linml setup` on WSL2 report the WSL2 build.
6. The 0.5.0 package is published to nuget.org (`LinML.Catalog`, `LinML.Contracts`; those are the only LinML packages on nuget.org).

## 9. Pitfalls checklist

- Never register the stock plugin on the WSL2 path; ORT cannot replace a registration within a process.
- Downloads happen only when `EnsureOptions.AllowDownloads` is true. Consumers (Trackdub) gate that on the NVIDIA license.
- Keep dependency handles alive; the loader may resolve the plugin's `NEEDED` entries lazily.
- Preloading depends on the SONAME symlinks in the stock directory (`libcudart.so.13`, `libtensorrt_rtx.so.1`, `libtensorrt_onnxparser_rtx.so.1`); `PluginInstaller.CreateRequiredSymlinks` creates them. Verify they exist on the overlay path too.
- Detect WSL2, not just WSL: WSL1 also has "microsoft" in `/proc/version` but no GPU. Require `/dev/dxg`.
- Do not change `IExecutionProvider`, `EnsureOptions`, `ProviderEnsureResult` shapes (binary and fake-implementation compatibility).
- The patched build is a prerelease from a fork, pinned at upstream PR #12's head. Keep the removal path documented: once NVIDIA ships a wheel with the fix, bump the stock pin and delete the overlay.

## 10. Trackdub follow-up after LinML ships (do not do this in the LinML repo)

Tracked in Trackdub after PR #471 merges and `LinML.Catalog` is bumped:

- Delete `LinMlWsl2TensorRtRtxPlugin` and its wiring/tests; remove the "restart" and "vendor the WSL2 build" hints; drop the `LinMlWsl2TensorRtRtxPluginTests` and the bootstrap WSL2 tests.
- Keep `LinuxWsl.HasNvidiaGpuPassthrough()` and the probe change: that is Trackdub's own NVIDIA hardware gate, which the catalog does not provide.
- Update `docs/reference/gpu-execution-providers.md` (TensorRT RTX on Linux → WSL2 bullet) and add the patched build to `docs/legal/THIRD_PARTY_NOTICES.md`.
- Re-run the WSL2 smoke: production factory → registered; `AppendTensorRtRtxOrFallbackProvider` selects TensorRT-RTX; model output as in section 4.

## 11. Open questions for the owner

1. Default on for WSL2, or opt-in (`LINML_TRT_RTX_WSL2=1`)? Recommendation: default on, opt-out available.
2. Is it acceptable for the catalog to download an artifact built from a fork (`tonythethompson/TensorRT-RTX-EP-ABI`) by default, or should it wait for upstream PR #12 / an NVIDIA wheel?
3. Should the overlay build move into CI (it is currently built locally by `scripts/build-trt-rtx-ep.sh` against TensorRT-RTX 1.6.1.120 headers and is byte-stable)?
4. Version: 0.5.0 (recommended) or a 0.4.1 patch?
