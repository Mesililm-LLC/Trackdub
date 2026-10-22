# Handoff: finish native WSL2 TensorRT-RTX support in `LinML.Catalog`

**For:** an agent working in `mesalimllc/LinML` (branch from `main`, current release **v0.4.1**).
**Why:** Trackdub consumes `LinML.Catalog` 0.4.1 and still carries a WSL2 workaround
(`LinMlWsl2TensorRtRtxPlugin`). v0.4.1 moved *selection and loading* of the patched build into the catalog but not
*installation*, so the catalog alone still cannot take a clean WSL2 host to a working GPU provider. This change closes
that gap so Trackdub can delete its workaround.

## 1. Goal

On WSL2 with NVIDIA GPU passthrough,
`ExecutionProviderCatalog.GetDefault().Find(ProviderId.TensorrtRtx)!.EnsureReadyAsync(new EnsureOptions(AllowDownloads: true))`
must end **Ready and exposing the GPU** from a clean host: no `linml setup --trt-rtx-wsl2` step, no `LD_LIBRARY_PATH`,
no `LINML_EP_LIBRARIES`, and no process restart between install and use.

## 2. What v0.4.1 already does (do not redo)

Verified against `main` @ `f4d8ad9`:

- `PluginExecutionProvider.LibraryPath` prefers a vendored WSL2 build over the stock wheel on WSL2 hosts
  (`WslVariantLibraryPath`, `PluginExecutionProvider.cs`), under the catalog's own root and, for the default catalog, the default roots.
- `TryRegister` preloads the **stock** wheel's runtime directory before registering the WSL2 build, and fails with a clear message
  ("... Run 'linml setup --trt-rtx-wsl2'") when the stock wheel is missing. Bundled-library handles are kept for the process lifetime.
- `KnownPlugins.TensorrtRtxWsl2` exists (public, deliberately not in `All`): release `ep-trt-rtx-wsl2-0.4.0-wsl2p1`, sha256
  `e87adedba3dfc86ea96cd7c6f5b9a965de584aeda971ee7792ae6d1e0074d2df`, install dir `ep-plugins/trt-rtx-wsl2/linml_trt_rtx_ep`,
  library `libonnxruntime_providers_nv_tensorrt_rtx.so`, min ORT 1.24.
- `linml setup --trt-rtx-wsl2` installs it (SHA-256 verified, installs the stock wheel first). Detected WSL2 + RTX hosts see it as an
  "Opt-in available" offer. Extraction keeps `PATCH-NOTICE.md` and `licenses/`.
- On WSL2 an inherited `LINML_EP_LIBRARIES` mapping to the stock wheel no longer hides an installed patched build.
- Tests: `tests/LinML.Catalog.Tests/WslVariantTests.cs`, `tests/LinML.Cli.Tests/SetupCommandTests.cs`, `ScriptPinAgreementTests.cs`.

## 3. What is still missing (the work)

1. **`EnsureReadyAsync` never installs the WSL2 build.** `PluginExecutionProvider.EnsureReadyAsync` only vendors `_spec` (the stock wheel)
   when `LibraryPath is null`. On WSL2 with downloads allowed and nothing vendored it installs **stock** and registers it. ORT accepts one
   registration per EP name per process, so the zero-device stock registration then blocks the patched build until the process restarts.
   The result is `NoDevices` with the text "Opt in to the patched WSL2 build with 'linml setup --trt-rtx-wsl2'".
2. **Stock-only vendored on WSL2 has the same trap.** If the stock wheel is vendored but the WSL2 build is not, `LibraryPath` returns the stock
   library and `TryRegister` registers it (zero devices) instead of fetching the patched build.
3. **`TRT_RTX_CUSTOM_EP` is honoured only by the CLI** (`ProviderArgs.cs`), not by the catalog, so library consumers cannot use it.
4. **WSL detection is loose for this purpose.** `DetectWsl()` returns true for any `/proc/version` containing "microsoft", so WSL1 (no GPU) qualifies.
   Downloading the patched build should additionally require `/dev/dxg` (WSL2 GPU passthrough).

## 4. Proposed change

Keep the WSL2 build a **variant of `TensorrtRtx`**, not a new `ProviderId` (same EP name; one registration per name).

Selection rule in `PluginExecutionProvider` (`Id == TensorrtRtx` only), highest precedence first:

1. `TRT_RTX_CUSTOM_EP` set to an existing file: use it (matches the CLI).
2. WSL2 (`IsWsl()` **and** `/dev/dxg` exists) and not opted out: use the WSL2 build.
3. Otherwise: the stock wheel exactly as today.

Opt-out: `LINML_TRT_RTX_WSL2=0` (suggested name). Recommended default is **on**: upstream's stock plugin provably exposes zero devices on WSL2.

`EnsureReadyAsync` when the WSL2 variant is selected:

1. Existing-devices short-circuit stays first.
2. Stock files missing: vendor stock via `PluginInstaller.InstallAsync` if `AllowDownloads`, else `DownloadDisallowed`. **Never register stock on this path.**
3. WSL2 library missing: vendor `KnownPlugins.TensorrtRtxWsl2` if `AllowDownloads`, else `DownloadDisallowed`.
4. Existing `TryRegister` logic (preload stock directory, register the WSL2 path).
5. Zero devices: `NoDevices` with a WSL2-specific message (check `nvidia-smi` and `/dev/dxg`). **Do not fall back to stock.**
6. `Downloaded` when step 2 or 3 downloaded; `Detail` names the build registered.

Reuse the installer's download, sha256 and staged `Promote` path. Keep the install layout exactly as is so existing script/CLI installs are reused.

**Do not break consumers.** Trackdub and its tests implement `IExecutionProvider` with fakes and construct `EnsureOptions(bool)`. Do not add members
to `IExecutionProvider`, change the `EnsureOptions` constructor, or change `ProviderEnsureResult`'s shape. If the active build must be observable, use a
default interface member or `ProviderEnsureResult.Detail` only.

## 5. Tests

Use the existing seams (`FakeRegistrar`, `FakeWheelHandler`, `TempDir`, `WslProbe`) and add injectable `/dev/dxg` and environment-variable readers.

- WSL2, nothing vendored, downloads allowed: downloads stock then WSL2 build, **never registers stock**, registers the WSL2 path, `Downloaded`.
- WSL2, stock vendored only, downloads allowed: downloads only the WSL2 build, no stock registration.
- WSL2, both vendored: no HTTP, preload uses the stock directory, one `Register(name, wslPath)`, `Ready`.
- WSL2, downloads disallowed, WSL2 build missing: `DownloadDisallowed`, no HTTP, no registration.
- WSL2, registers but zero devices: `NoDevices`, WSL2 message, no stock fallback.
- WSL1 (no `/dev/dxg`): stock path, no WSL2 download.
- Not WSL: identical to 0.4.1 for all three plugins (regression guard).
- `LINML_TRT_RTX_WSL2=0` selects stock. `TRT_RTX_CUSTOM_EP` beats the WSL2 build.
- sha256 mismatch on the WSL2 build is reported as `Failed`, like the stock wheel.
- `ScriptPinAgreementTests` stays green (the pin already matches `fetch_trt_rtx_wsl2`).
- Opt-in end-to-end test, skipped unless a WSL2 GPU is present, modelled on `OpenVinoCatalogEndToEndTests`.

## 6. Acceptance criteria

1. `dotnet test` green for `LinML.Catalog.Tests`, `LinML.Runtime.Onnx.Tests`, `LinML.Cli.Tests`.
2. WSL2 + NVIDIA GPU, **nothing vendored**: the one-liner in section 1 returns `Downloaded` with `Devices.Count >= 1`, then
   `AppendFromCatalog(..., [OrtHardwareDeviceType.GPU])` and `tests/fixtures/model.onnx` (input `x` = [1..6], shape [3,2]) yields `[11, 22, 13, 24, 15, 26]`. No env vars, no restart.
3. WSL2 host with only the stock wheel vendored: same result, stock is never registered.
4. WSL2 host with both vendored by the script/CLI: no download, `Ready`.
5. Bare-metal Linux and non-WSL hosts: behaviour unchanged.
6. New version published to nuget.org (`LinML.Catalog`, `LinML.Contracts`) and GitHub Packages (`release.yml` publishes to both). Suggest **0.4.2**
   (no API change) or 0.5.0 if the new default-on WSL2 behaviour is treated as breaking. Add a `CHANGELOG.md` entry and update `docs/compatibility.md`,
   `docs/wsl-workflow.md`, `README.md`, `src/LinML.Catalog/README.md` and ADR 0002 ("opt-in" becomes "default on, opt-out").

## 7. Pitfalls

- Never register the stock plugin on the WSL2 path; ORT cannot replace a registration within a process.
- Downloads happen only when `EnsureOptions.AllowDownloads` is true. Trackdub gates that on the NVIDIA TensorRT-RTX license acceptance, which is why
  it must stay an explicit caller choice.
- The patched build `NEEDED`s `libcudart.so.13`, `libtensorrt_rtx.so.1`, `libtensorrt_onnxparser_rtx.so.1` from the stock directory (SONAME symlinks from
  `PluginInstaller.CreateRequiredSymlinks`), plus the system `libcuda.so.1` and `libnvidia-ml.so.1` under `/usr/lib/wsl/lib`.
- The patched build is a prerelease from a fork (`tonythethompson/TensorRT-RTX-EP-ABI`, upstream PR #12 head, issue #13). Keep the removal path documented:
  once NVIDIA ships a wheel with the fix, bump the stock pin and delete the variant.
- Containers: the catalog install root is `$HOME/.local/linml-deps` (`ExecutionProviderCatalog.DefaultInstallRoot`) unless `Create(root)` is used;
  `LINML_DEPS` and `LINML_PLUGIN_ROOT` only help *find* vendored libraries. Document this for Docker users (a volume at that path keeps installs across runs).

## 8. Trackdub follow-up after LinML ships (not in the LinML repo)

- Bump `LinML.Catalog` in `Directory.Packages.props`, regenerate lock files.
- Delete `LinMlWsl2TensorRtRtxPlugin` and its wiring and tests (`LinMlWsl2TensorRtRtxPluginTests`, the WSL2 cases in `LinMlTensorRtRtxBootstrapTests`);
  remove the `StockMissing`/restart handling in `LinMlTensorRtRtxBootstrap`.
- Keep `LinuxWsl.HasNvidiaGpuPassthrough()` and the probe change: that is Trackdub's own NVIDIA hardware gate.
- **Before the catalog change ships**, two small Trackdub fixes are worthwhile: change `LinMlWsl2TensorRtRtxPlugin.InstallHint` to
  `linml setup --trt-rtx-wsl2` (the `scripts/fetch-ep-plugins.sh` path it cites is not available to NuGet or Docker users), and link LinML's
  `docs/wsl-workflow.md` and `docs/compatibility.md` from `docs/reference/gpu-execution-providers.md`.
- Add the patched build to `docs/legal/THIRD_PARTY_NOTICES.md`; re-run the WSL2 smoke (production factory registers; `AppendTensorRtRtxOrFallbackProvider` selects TensorRT-RTX).

## 9. Open questions for the owner

1. Default on for WSL2, or opt-in (`LINML_TRT_RTX_WSL2=1`)? Recommendation: default on, opt-out available.
2. Is it acceptable for the catalog to download an artifact built from a fork by default, or should it wait for upstream PR #12 or an NVIDIA wheel?
3. Should the patched build move into CI (today it is built locally by `scripts/build-trt-rtx-ep.sh` against TensorRT-RTX 1.6.1.120 headers and is byte-stable)?
4. Version: 0.4.2 or 0.5.0?
